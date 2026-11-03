using System.Buffers.Binary;
using System.Net;
using System.Text;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Security;
using MatDock.Core.Ssh;
using MatDock.Core.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// One MatDock as the bridge of another (<see cref="BridgeProtocol"/>): the other sends its Docker commands over HTTP, this one runs them on one of its
/// environments and streams the answer back. Here the "other side" is a MatDock in memory whose Docker is <see cref="FakeHostSessionFactory"/>.
/// </summary>
public sealed class BridgeTests : IClassFixture<MatDockFactory>, IDisposable
{
    private const string SelfId = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private readonly MatDockFactory _site;
    private readonly string _key;

    public BridgeTests(MatDockFactory site)
    {
        _site = site;
        _site.Hosts.StreamResults = true;
        _site.Hosts.Respond = c => c.Contains("say-hello", StringComparison.Ordinal) ? "hello from server2\n" : null;
        _site.Hosts.Fails = c => c.Contains("fail-please", StringComparison.Ordinal);
        _site.Db(db =>
        {
            if (!db.Environments.Any(e => e.Name == "local"))
            {
                db.Environments.Add(new DockerEnvironment { Name = "local", ConnectionType = ConnectionType.Local, Host = "local", Username = "local", IsEnabled = true, UseSudo = true });
                db.SaveChanges();
            }

            return 0;
        });
        using var scope = _site.Services.CreateScope();
        _key = scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("server1", ApiKeyScope.Bridge, null).GetAwaiter().GetResult().Key!;
        _site.Services.GetRequiredService<SelfContainer>().Id = SelfId;
        BridgeHttp.HandlerForTests = () => _site.Server.CreateHandler();
    }

    public void Dispose()
    {
        BridgeHttp.HandlerForTests = null;
        _site.Hosts.StreamResults = false;
        _site.Hosts.Respond = null;
        _site.Hosts.Fails = null;
        _site.Services.GetRequiredService<SelfContainer>().Id = null;
    }

    private SshConnectionSettings Bridge(string? key = null, string environment = "local")
        => new() { ConnectionType = ConnectionType.Bridge, Host = "http://localhost/", Username = string.Empty, Password = key ?? _key, RemoteEnvironment = environment };

    private static IHostSession Session(SshConnectionSettings settings) => new HostSessionFactory(null!).Create(settings);

    private static async Task<List<(BridgeProtocol.Frame Type, byte[] Payload)>> FramesAsync(HttpResponseMessage response)
    {
        var frames = new List<(BridgeProtocol.Frame, byte[])>();
        await using var stream = await response.Content.ReadAsStreamAsync();
        while (await BridgeProtocol.ReadFrameAsync(stream, CancellationToken.None) is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    private HttpRequestMessage Exec(string command, string stdin = "", string? key = null, string environment = "local")
    {
        var body = BridgeProtocol.CommandHead(command).Concat(Encoding.UTF8.GetBytes(stdin)).ToArray();
        var request = new HttpRequestMessage(HttpMethod.Post, BridgeProtocol.ExecPath + "?environment=" + environment) { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Api-Key", key ?? _key);
        return request;
    }

    [Fact]
    public async Task A_frame_and_a_command_come_back_as_they_went()
    {
        var stream = new MemoryStream();
        await BridgeProtocol.WriteFrameAsync(stream, BridgeProtocol.Frame.Stdout, Encoding.UTF8.GetBytes("ab"), CancellationToken.None);
        await BridgeProtocol.WriteFrameAsync(stream, BridgeProtocol.Frame.Exit, new byte[] { 0, 0, 0, 3 }, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal("ab", Encoding.UTF8.GetString((await BridgeProtocol.ReadFrameAsync(stream, CancellationToken.None))!.Value.Payload));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian((await BridgeProtocol.ReadFrameAsync(stream, CancellationToken.None))!.Value.Payload));
        Assert.Null(await BridgeProtocol.ReadFrameAsync(stream, CancellationToken.None));

        var body = new MemoryStream(BridgeProtocol.CommandHead("docker ps 'ä'").Concat(new byte[] { 9, 9 }).ToArray());
        Assert.Equal("docker ps 'ä'", await BridgeProtocol.ReadCommandAsync(body, CancellationToken.None));
        Assert.Equal(9, body.ReadByte()); // the stdin follows
    }

    [Theory]
    [InlineData("https://server2:4455", true)]
    [InlineData("http://10.0.0.2:4455/", true)]
    [InlineData("server2:4455", false)]
    [InlineData("ftp://server2", false)]
    [InlineData("https://me:pw@server2", false)]
    public void The_address_of_a_bridge_is_an_http_address_without_credentials(string url, bool ok)
        => Assert.Equal(ok, BridgeProtocol.Problem(url) is null);

    [Fact]
    public async Task A_command_runs_on_the_named_environment_with_its_stdin_and_streams_its_answer_back()
    {
        using var client = _site.CreateClient();
        using var response = await client.SendAsync(Exec("docker say-hello", stdin: "the compose file"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var frames = await FramesAsync(response);
        Assert.Equal("hello from server2\n", Encoding.UTF8.GetString(frames.Single(f => f.Type == BridgeProtocol.Frame.Stdout).Payload));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(frames.Last().Payload));
        Assert.Equal(BridgeProtocol.Frame.Exit, frames.Last().Type);
        Assert.Contains("docker say-hello", _site.Hosts.Commands);
        Assert.Contains("the compose file", _site.Hosts.Inputs);
    }

    [Fact]
    public async Task Only_a_bridge_key_opens_the_bridge_and_a_bridge_key_opens_nothing_else()
    {
        using var client = _site.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Exec("docker ps", key: MatDockFactory.EngineKey))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Exec("docker ps", key: "wrong-key-0123456789"))).StatusCode);

        using var scope = _site.Services.CreateScope();
        var full = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("script", ApiKeyScope.Full, null)).Key!;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Exec("docker ps", key: full))).StatusCode);

        var status = await _site.Api(_key).GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.Contains("bridge key", await status.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await _site.Api(_key).GetAsync("/api/v1/stacks")).StatusCode);
    }

    [Fact]
    public async Task A_chain_of_more_than_four_bridges_is_refused_and_an_unknown_environment_named()
    {
        using var client = _site.CreateClient();
        var looping = Exec("docker ps");
        looping.Headers.Add(BridgeProtocol.HopsHeader, "5");
        Assert.Equal(HttpStatusCode.LoopDetected, (await client.SendAsync(looping)).StatusCode);

        var unknown = await client.SendAsync(Exec("docker ps", environment: "nowhere"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("nowhere", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Through_the_client_a_command_works_as_over_SSH_buffered_streamed_with_stdin_and_failing()
    {
        using var session = Session(Bridge());
        await session.ConnectAsync(CancellationToken.None);

        using (var buffered = session.CreateCommand("docker say-hello"))
        {
            Assert.Equal("hello from server2\n", buffered.Execute());
            Assert.Equal(0, buffered.ExitStatus);
        }

        using (var streamed = session.CreateCommand("docker say-hello --with-stdin"))
        {
            streamed.BufferOutput = false;
            var started = streamed.BeginExecute();
            await using (var input = streamed.CreateInputStream())
            {
                await input.WriteAsync(Encoding.UTF8.GetBytes("a tar of the repository"));
            }

            Assert.Equal("hello from server2\n", await new StreamReader(streamed.OutputStream).ReadToEndAsync());
            streamed.EndExecute(started);
            Assert.Equal(0, streamed.ExitStatus);
            Assert.Contains("a tar of the repository", _site.Hosts.Inputs);
        }

        using (var failing = session.CreateCommand("docker fail-please"))
        {
            failing.Execute();
            Assert.Equal(1, failing.ExitStatus);
            Assert.Equal("simulated failure", failing.Error);
        }

        using var refused = Session(Bridge(key: "wrong-key-0123456789")).CreateCommand("docker ps");
        refused.Execute();
        Assert.Equal(-1, refused.ExitStatus);
        Assert.Contains("does not accept the bridge key", refused.Error);
    }

    [Fact]
    public async Task The_bridge_says_how_Docker_is_called_there_and_which_container_is_its_MatDock()
    {
        var info = await BridgeHttp.InfoAsync(Bridge(), CancellationToken.None);
        Assert.Equal(new BridgeInfo("local", "local", true, null, SelfId, 0, info.Version), info);

        var offered = await BridgeHttp.EnvironmentsAsync(Bridge(), CancellationToken.None);
        Assert.Contains(offered, e => e.Name == "local" && e.Connection == "local");

        // So a deploy of the other MatDock's own stack through the bridge goes to a helper container there, with its mounts.
        var target = await new SelfContainer(null).TargetAsync(7, Bridge(), CancellationToken.None);
        Assert.Equal(SelfId, target!.Id);
        Assert.Equal("--volumes-from " + SelfId, target.HandOver()!.DockerAccess);
    }

    [Fact]
    public async Task A_stack_on_a_bridge_environment_is_deployed_on_the_other_MatDock_with_its_files_on_stdin_and_its_docker_head()
    {
        // server1: its own database, an environment that is the bridge to the MatDock in memory (server2, whose "local" needs sudo).
        using var db = new TestDatabase();
        var options = Options.Create(new MatDockOptions());
        var secrets = new FakeSecretProtector();
        var environments = new EnvironmentService(db.Context, secrets, new EnvironmentConnectionService(new HostSessionFactory(null!), options,
            NullLogger<EnvironmentConnectionService>.Instance), options);
        var env = new DockerEnvironment
        {
            Name = "server2", ConnectionType = ConnectionType.Bridge, Host = "http://localhost", EncryptedPassword = secrets.Protect(_key), RemoteEnvironment = "local",
            IsEnabled = true, Status = EnvironmentStatus.Online,
        };
        db.Context.Environments.Add(env);
        db.Context.Stacks.Add(new Stack { Name = "shop", EnvironmentId = 0, ComposeYaml = "services:\n  web:\n    image: nginx:1\n" });
        db.Context.SaveChanges();
        var stack = db.Context.Stacks.Single();
        stack.EnvironmentId = env.Id;
        db.Context.SaveChanges();

        // The connection test learns from server2 how Docker is called there.
        _site.Hosts.Respond = c => c.Contains("version --format", StringComparison.Ordinal)
            ? "{\"Version\":\"27.3.1\",\"ApiVersion\":\"1.47\",\"Os\":\"linux\",\"Arch\":\"amd64\"}"
            : null;
        var test = await environments.TestAndPersistAsync(env.Id);
        Assert.True(test.Success, test.Message);
        Assert.Contains("through the MatDock at http://localhost", test.Message);
        Assert.True(db.Context.Environments.AsNoTracking().Single().UseSudo);

        var stacks = new StackService(db.Context, new HostSessionFactory(null!), environments, new MatDock.Core.Git.GitCredentialService(db.Context, secrets),
            new MatDock.Core.Git.GitRepositoryService(NullLogger<MatDock.Core.Git.GitRepositoryService>.Instance),
            new MatDock.Core.Registries.RegistryService(db.Context, secrets, new MatDock.Core.Registries.RegistryApiClient(new NoHttp(), NullLogger<MatDock.Core.Registries.RegistryApiClient>.Instance)),
            new NoopScheduleEventBus(), options, NullLogger<StackService>.Instance);
        var (ok, output) = await stacks.DeployAsync(stack.Id);

        Assert.True(ok, output);
        var deploy = Assert.Single(_site.Hosts.Commands, c => c.Contains("cat > \"$dir/docker-compose.yml\"", StringComparison.Ordinal));
        Assert.Contains("sudo -n docker compose -p \"$name\" $cf up -d", deploy); // the head of server2's environment
        Assert.Contains(_site.Hosts.Inputs, i => i.Contains("image: nginx:1", StringComparison.Ordinal));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
