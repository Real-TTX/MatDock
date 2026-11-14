using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using MatDock.Core.Stacks;
using MatDock.Web.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// MatDock as an MCP server at /mcp, talked to by a real MCP client: only with a key; a read key sees and calls only the tools that change nothing; a full
/// key acts, and each change is in the activity log with the key's name; the .env of a stack never reaches a read key.
/// </summary>
public sealed class McpServerTests : IClassFixture<McpServerTests.Site>
{
    private const string Inspect = """[{"Id":"webid0000001","Name":"/web","Created":"2026-10-20T10:00:00Z","Config":{"Image":"nginx:1.27","Env":["DB_PASSWORD=hunter2","TZ=UTC"],"Labels":{}},"State":{"Status":"running","Running":true,"ExitCode":0},"HostConfig":{"RestartPolicy":{"Name":"always"}},"NetworkSettings":{"Networks":{}},"Mounts":[]}]""";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("mcp-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new { ID = "webid0000001", Names = "web", Image = "nginx:1.27", State = "running", Status = "Up 1 hour", Labels = "com.docker.compose.project=shop,com.docker.compose.service=web", Ports = string.Empty }));
            App.Hosts.Respond = command =>
                command.Contains(" inspect --type container ", StringComparison.Ordinal) ? Inspect
                : command.Contains(" exec ", StringComparison.Ordinal) ? "total 0\ndrwxr-xr-x data\n"
                : null;

            using var scope = App.Services.CreateScope();
            var (_, _, stack, _) = scope.ServiceProvider.GetRequiredService<StackService>()
                .UpsertInlineAsync(EnvId, "shop", "services:\n  web:\n    image: nginx:1.27\n", "DB_PASSWORD=hunter2", null).GetAwaiter().GetResult();
            StackId = stack!.Id;
            var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
            ReadKey = keys.CreateAsync("claude-read", ApiKeyScope.Read, null).GetAwaiter().GetResult().Key!;
            FullKey = keys.CreateAsync("claude", ApiKeyScope.Full, null).GetAwaiter().GetResult().Key!;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }
        public long StackId { get; }
        public string ReadKey { get; }
        public string FullKey { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public McpServerTests(Site site) => _site = site;

    private async Task<McpClient> ClientAsync(string key)
    {
        var http = _site.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
    }

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments ?? []);
        return (result.IsError == true, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
    }

    [Fact]
    public async Task Without_a_key_there_is_no_mcp()
    {
        var response = await _site.App.CreateClient().PostAsync("/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_read_key_sees_and_calls_only_the_tools_that_change_nothing()
    {
        await using var client = await ClientAsync(_site.ReadKey);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("get_status", tools);
        Assert.Contains("list_containers", tools);
        Assert.Contains("container_logs", tools);
        Assert.DoesNotContain("container_action", tools);
        Assert.DoesNotContain("exec_in_container", tools);
        Assert.DoesNotContain("deploy_stack", tools);
        Assert.Equal(McpToolCatalog.All.Count(t => t.Value), tools.Count);

        _site.App.Hosts.Commands.Clear();
        var (error, text) = await CallAsync(client, "container_action", new() { ["container"] = "web", ["action"] = "stop" });
        Assert.True(error);
        Assert.Contains("may only read", text);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" stop ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_key_sees_every_tool_and_reads_the_engine()
    {
        await using var client = await ClientAsync(_site.FullKey);
        Assert.Equal(McpToolCatalog.All.Count, (await client.ListToolsAsync()).Count);

        var (_, status) = await CallAsync(client, "get_status");
        Assert.Contains("\"name\":\"mcp-host\"", status);

        var (_, containers) = await CallAsync(client, "list_containers", new() { ["environment"] = "mcp-host" });
        Assert.Contains("\"name\":\"web\"", containers, StringComparison.OrdinalIgnoreCase);

        var (_, inspect) = await CallAsync(client, "inspect_container", new() { ["container"] = "web" });
        Assert.Contains("always", inspect);
        Assert.DoesNotContain("hunter2", inspect);
    }

    [Fact]
    public async Task A_change_reaches_docker_and_is_in_the_activity_log_with_the_key()
    {
        await using var client = await ClientAsync(_site.FullKey);
        _site.App.Hosts.Commands.Clear();

        var (error, text) = await CallAsync(client, "container_action", new() { ["container"] = "web", ["action"] = "restart" });

        Assert.False(error, text);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" restart 'web'", StringComparison.Ordinal));
        var entry = _site.App.Db(db => db.ActivityEntries.Where(a => a.Source == "mcp").OrderByDescending(a => a.Id).First());
        Assert.Equal(("API key claude", "MCP · container_action", "web", true), (entry.Actor, entry.Action, entry.Target, entry.Ok));
    }

    [Fact]
    public async Task Exec_runs_one_quoted_command_in_the_container()
    {
        await using var client = await ClientAsync(_site.FullKey);
        _site.App.Hosts.Commands.Clear();

        var (error, text) = await CallAsync(client, "exec_in_container", new() { ["container"] = "web", ["command"] = "ls -la /data; echo 'x'" });

        Assert.False(error, text);
        Assert.Contains("drwxr-xr-x data", text);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" exec 'web' sh -c 'ls -la /data; echo '\\''x'\\''' 2>&1", StringComparison.Ordinal));

        var (badUser, message) = await CallAsync(client, "exec_in_container", new() { ["container"] = "web", ["command"] = "id", ["user"] = "root; reboot" });
        Assert.True(badUser);
        Assert.Contains("Invalid user", message);
    }

    [Fact]
    public async Task A_deploy_waits_for_its_operation_and_answers_the_outcome()
    {
        await using var client = await ClientAsync(_site.FullKey);
        _site.App.Hosts.Commands.Clear();

        var (error, text) = await CallAsync(client, "deploy_stack", new()
        {
            ["name"] = "cache", ["composeYaml"] = "services:\n  redis:\n    image: redis:7\n", ["waitSeconds"] = 30,
        });

        Assert.False(error, text);
        Assert.Contains("\"status\":\"succeeded\"", text);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains("up -d", StringComparison.Ordinal));
        Assert.NotNull(_site.App.Db(db => db.Stacks.SingleOrDefault(s => s.Name == "cache")));

        var (invalid, why) = await CallAsync(client, "deploy_stack", new() { ["name"] = "broken", ["composeYaml"] = "- just a list" });
        Assert.True(invalid);
        Assert.Contains("mapping", why);
    }

    [Fact]
    public async Task The_env_of_a_stack_reaches_only_a_key_that_may_change_it()
    {
        await using (var reader = await ClientAsync(_site.ReadKey))
        {
            var (_, text) = await CallAsync(reader, "get_stack", new() { ["name"] = "shop" });
            Assert.Contains("services:", text);
            Assert.DoesNotContain("hunter2", text);
        }

        await using (var writer = await ClientAsync(_site.FullKey))
        {
            var (_, text) = await CallAsync(writer, "get_stack", new() { ["name"] = "shop" });
            Assert.Contains("hunter2", text);
        }

        // The REST API had the same gap: a read key no longer gets the .env.
        var rest = await (await _site.App.Api(_site.ReadKey).GetAsync($"/api/v1/stacks/shop?environment={_site.EnvId}")).Content.ReadAsStringAsync();
        Assert.Contains("\"envContent\":null", rest);
        var full = await (await _site.App.Api(_site.FullKey).GetAsync($"/api/v1/stacks/shop?environment={_site.EnvId}")).Content.ReadAsStringAsync();
        Assert.Contains("hunter2", full);
    }

    [Fact]
    public async Task With_several_environments_the_environment_has_to_be_named()
    {
        var other = _site.App.AddLocalEnvironment("mcp-second");
        try
        {
            await using var client = await ClientAsync(_site.FullKey);
            var (error, text) = await CallAsync(client, "list_containers");
            Assert.True(error);
            Assert.Contains("mcp-host", text);
            Assert.Contains("mcp-second", text);
        }
        finally
        {
            _site.App.Db(db =>
            {
                db.Environments.Remove(db.Environments.Single(e => e.Id == other));
                return db.SaveChanges();
            });
        }
    }

    [Fact]
    public void The_catalog_names_every_tool_once_and_marks_what_changes_something()
    {
        var all = McpToolCatalog.All;
        Assert.True(all.Count >= 40, $"{all.Count} tools");
        Assert.All(all.Keys, name => Assert.Matches("^[a-z]+(_[a-z]+)*$", name));
        foreach (var write in new[] { "container_action", "remove_container", "run_container", "exec_in_container", "deploy_stack", "stack_action", "remove_stack", "remove_volume", "restore_backup", "cleanup_engine", "set_update_rule", "run_schedule", "apply_updates" })
        {
            Assert.False(McpToolCatalog.IsReadOnly(write), write);
        }

        foreach (var read in new[] { "get_status", "list_containers", "inspect_container", "container_logs", "list_stacks", "get_stack", "list_events", "get_metrics", "get_operation", "list_activity", "list_updates", "check_updates", "check_stack_updates" })
        {
            Assert.True(McpToolCatalog.IsReadOnly(read), read);
        }

        Assert.False(McpToolCatalog.IsReadOnly("no_such_tool"));
    }

    [Fact]
    public void Exec_quotes_the_command_and_checks_the_user()
    {
        Assert.Equal(" exec --user 'www-data' 'web' sh -c 'cat /etc/os-release' 2>&1", ContainerCommands.Exec("docker", "web", "cat /etc/os-release", "www-data")[^(" exec --user 'www-data' 'web' sh -c 'cat /etc/os-release' 2>&1".Length)..]);
        Assert.Contains(" exec --user '1000:1000' 'web'", ContainerCommands.Exec("docker", "web", "id", "1000:1000"));
        Assert.Throws<ArgumentException>(() => ContainerCommands.Exec("docker", "web", "id", "root; reboot"));
        Assert.Throws<ArgumentException>(() => ContainerCommands.Exec("docker", "web", " "));
        Assert.Throws<ArgumentException>(() => ContainerCommands.Exec("docker", "web'; id", "id"));
    }
}
