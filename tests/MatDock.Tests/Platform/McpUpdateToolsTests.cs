using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using MatDock.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// The updates of all stacks as MCP tools: a read key sees what the last look found and may look again (nothing on a host changes), only a full key
/// updates; a stack MatDock did not deploy is updated by <c>stack_action</c> like a managed one.
/// </summary>
public sealed class McpUpdateToolsTests : IClassFixture<McpUpdateToolsTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("mcp-updates");
            App.Hosts.Containers.Add(Container("shop", "localhost:1/app:1.2.0"));
            App.Hosts.Containers.Add(Container("blog", "localhost:1/blog:latest"));
            App.Api(MatDockFactory.EngineKey).PutAsJsonAsync("/api/v1/stacks/shop", new { composeYaml = "services:\n  web:\n    image: localhost:1/app:1.2.0\n" })
                .GetAwaiter().GetResult().EnsureSuccessStatusCode();
            using var scope = App.Services.CreateScope();
            var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
            ReadKey = keys.CreateAsync("updates-read", ApiKeyScope.Read, null).GetAwaiter().GetResult().Key!;
            FullKey = keys.CreateAsync("updates-full", ApiKeyScope.Full, null).GetAwaiter().GetResult().Key!;
        }

        private static string Container(string project, string image)
            => JsonSerializer.Serialize(new
            {
                ID = project + "00000001", Names = project + "-web-1", Image = image, State = "running", Status = "Up 3 hours",
                Labels = $"com.docker.compose.project={project},com.docker.compose.service=web", Ports = string.Empty,
            });

        public MatDockFactory App { get; }
        public long EnvId { get; }
        public string ReadKey { get; }
        public string FullKey { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public McpUpdateToolsTests(Site site) => _site = site;

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
    public async Task A_read_key_sees_the_overview_and_looks_again_and_only_a_full_key_updates()
    {
        await using var reader = await ClientAsync(_site.ReadKey);
        var tools = (await reader.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("list_updates", tools);
        Assert.Contains("check_updates", tools);
        Assert.DoesNotContain("apply_updates", tools);

        var (listError, list) = await CallAsync(reader, "list_updates");
        Assert.False(listError, list);
        using (var rows = JsonDocument.Parse(list))
        {
            var byName = rows.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("stack").GetString()!);
            Assert.True(byName["shop"].GetProperty("managed").GetBoolean());
            Assert.False(byName["blog"].GetProperty("managed").GetBoolean()); // only found on the host
            Assert.Equal("mcp-updates", byName["blog"].GetProperty("environment").GetString());
        }

        _site.App.Hosts.Commands.Clear();
        var (checkError, check) = await CallAsync(reader, "check_updates", new() { ["environment"] = "mcp-updates" });
        Assert.False(checkError, check);
        using (var answer = JsonDocument.Parse(check))
        {
            Assert.Equal("succeeded", answer.RootElement.GetProperty("check").GetProperty("status").GetString());
            Assert.Contains("blog on mcp-updates: up to date.", answer.RootElement.GetProperty("check").GetProperty("output").GetString());
            Assert.Equal(0, answer.RootElement.GetProperty("pending").GetArrayLength());
        }

        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" pull", StringComparison.Ordinal) || c.Contains(" up -d", StringComparison.Ordinal)); // a look changes nothing

        var (applyError, apply) = await CallAsync(reader, "apply_updates");
        Assert.True(applyError);
        Assert.Contains("may only read", apply);
    }

    [Fact]
    public async Task A_full_key_updates_all_that_the_last_look_found_and_it_is_in_the_activity_log()
    {
        await using var client = await ClientAsync(_site.FullKey);
        var cache = _site.App.Services.GetRequiredService<UpdateCheckCache>();
        var none = new StackUpdatePlan([], null);
        cache.SetPlan(_site.EnvId, "shop", none);
        cache.SetPlan(_site.EnvId, "blog", none);

        var (_, nothing) = await CallAsync(client, "apply_updates");
        Assert.Contains("Nothing to update", nothing);

        cache.SetPlan(_site.EnvId, "blog", new StackUpdatePlan([new PlannedUpdate("web", "localhost:1/blog:latest", "bbbbbbbbbbbb", PlannedKind.Digest)], null));
        var (error, text) = await CallAsync(client, "apply_updates", new() { ["environment"] = "mcp-updates" });

        Assert.False(error, text);
        using var answer = JsonDocument.Parse(text);
        Assert.Equal(1, answer.RootElement.GetProperty("stacks").GetInt32());
        Assert.Equal("succeeded", answer.RootElement.GetProperty("update").GetProperty("status").GetString());
        var entry = _site.App.Db(db => db.ActivityEntries.Where(a => a.Source == "mcp").OrderByDescending(a => a.Id).First());
        Assert.Equal(("API key updates-full", "MCP · apply_updates", true), (entry.Actor, entry.Action, entry.Ok));
    }

    [Fact]
    public async Task A_stack_matdock_did_not_deploy_is_updated_by_stack_action()
    {
        await using var client = await ClientAsync(_site.FullKey);

        var (error, text) = await CallAsync(client, "stack_action", new() { ["name"] = "blog", ["action"] = "update" });

        Assert.False(error, text);
        using var answer = JsonDocument.Parse(text);
        Assert.Equal(("update", "blog", "succeeded"), (answer.RootElement.GetProperty("kind").GetString(), answer.RootElement.GetProperty("target").GetString(), answer.RootElement.GetProperty("status").GetString()));
        Assert.Contains("blog on mcp-updates: up to date.", answer.RootElement.GetProperty("output").GetString()); // the registry did not move the tag

        var (refused, why) = await CallAsync(client, "stack_action", new() { ["name"] = "blog", ["action"] = "redeploy" });
        Assert.True(refused);
        Assert.Contains("only start, stop, restart and update are possible", why);
    }
}
