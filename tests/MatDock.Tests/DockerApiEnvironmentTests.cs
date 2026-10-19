using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>Environments behind a Docker API (tcp://host:port, TLS client certificates).</summary>
public class DockerApiEnvironmentTests
{
    private static EnvironmentService Service(TestDatabase db)
        => new(db.Context, new FakeSecretProtector(), new FakeConnectionService(), Options.Create(new MatDockOptions()));

    private static EnvironmentInput Input(string? key = "KEY") => new()
    {
        Name = "api", ConnectionType = ConnectionType.Tcp, Host = "docker.example.org", Port = 2376,
        TlsCa = "CA\r\n", TlsCert = "CERT", TlsKey = key,
    };

    [Fact]
    public async Task The_key_is_encrypted_kept_on_a_blank_edit_and_dropped_with_another_type()
    {
        using var db = new TestDatabase();
        var service = Service(db);
        var env = await service.CreateAsync(Input());
        Assert.Equal("enc:KEY\n", env.EncryptedTlsKey);
        Assert.Equal("CA\n", env.TlsCa);
        Assert.Null(env.EncryptedPassword);

        await service.UpdateAsync(env.Id, Input(key: null));
        var settings = service.BuildSettings((await service.GetAsync(env.Id))!);
        Assert.True(settings.IsTcp && settings.RunsLocally);
        Assert.Equal("KEY\n", settings.TlsKeyPem);
        Assert.Equal("CERT\n", settings.TlsCertPem);
        Assert.Equal("KEY\n", (await service.BuildTestSettingsAsync(Input(key: ""), env.Id)).TlsKeyPem);

        await service.UpdateAsync(env.Id, new EnvironmentInput { Name = "api", ConnectionType = ConnectionType.Local });
        var local = (await service.GetAsync(env.Id))!;
        Assert.Null(local.EncryptedTlsKey);
        Assert.Null(local.TlsCa);
    }

    [Fact]
    public void A_session_points_the_cli_at_the_api_and_removes_its_certificates()
    {
        var settings = new SshConnectionSettings
        {
            ConnectionType = ConnectionType.Tcp, Host = "10.0.0.5", Port = 2376, Username = "", TlsCaPem = "ca", TlsCertPem = "cert", TlsKeyPem = "key",
        };
        string dir;
        using (var session = LocalHostSession.ForDockerApi(null, settings))
        {
            var docker = (IReadOnlyDictionary<string, string>)typeof(LocalHostSession)
                .GetField("_docker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
            Assert.Equal("tcp://10.0.0.5:2376", docker["DOCKER_HOST"]);
            Assert.Equal("1", docker["DOCKER_TLS_VERIFY"]);
            dir = docker["DOCKER_CERT_PATH"];
            Assert.Equal("key", File.ReadAllText(Path.Combine(dir, "key.pem")));
            Assert.Equal("ca", File.ReadAllText(Path.Combine(dir, "ca.pem")));
        }

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void An_ipv6_host_gets_brackets_and_plain_tcp_no_certificates()
    {
        using var session = LocalHostSession.ForDockerApi(null, new SshConnectionSettings { ConnectionType = ConnectionType.Tcp, Host = "fd00::5", Port = 2375, Username = "" });
        var docker = (IReadOnlyDictionary<string, string>)typeof(LocalHostSession)
            .GetField("_docker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
        Assert.Equal("tcp://[fd00::5]:2375", docker["DOCKER_HOST"]);
        Assert.False(docker.ContainsKey("DOCKER_CERT_PATH"));
    }

    [Fact]
    public void Host_readings_come_from_a_container_on_that_host()
    {
        var cmd = HostStats.BuildCommandInContainer("docker", "busybox");
        Assert.Contains("docker run --rm 'busybox' sh -c ", cmd);
        Assert.Contains("/proc/meminfo", cmd);
        Assert.EndsWith("echo CONTAINERS=$(docker ps -q 2>/dev/null | wc -l | tr -d ' ')", cmd);
    }
}
