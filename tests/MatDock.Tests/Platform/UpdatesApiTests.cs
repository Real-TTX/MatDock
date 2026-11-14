using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Operations;
using MatDock.Core.Security;
using MatDock.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// The updates of all stacks over <c>/api/v1</c>: the overview with the managed and the discovered stacks, a look at all of them and the update of all
/// that have something as operations, and the update of a discovered stack by its action. A read key reads the overview and starts nothing.
/// </summary>
public sealed class UpdatesApiTests : IClassFixture<UpdatesApiTests.Site>
{
    private const string Compose = "services:\n  web:\n    image: localhost:1/app:1.2.0\n";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("upd-host");
            App.Hosts.Containers.Add(Container("shop", "localhost:1/app:1.2.0"));
            App.Hosts.Containers.Add(Container("blog", "localhost:1/blog:latest",
                ",com.docker.compose.project.working_dir=/opt/stacks/blog,com.docker.compose.project.config_files=/opt/stacks/blog/compose.yaml"));
            App.Api(MatDockFactory.EngineKey).PutAsJsonAsync("/api/v1/stacks/shop", new { composeYaml = Compose }).GetAwaiter().GetResult().EnsureSuccessStatusCode();
            using var scope = App.Services.CreateScope();
            ReadKey = scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("updates-read", ApiKeyScope.Read, null).GetAwaiter().GetResult().Key!;
        }

        public static string Container(string project, string image, string labels = "", string id = "00000001", string status = "Up 3 hours")
            => JsonSerializer.Serialize(new
            {
                ID = project + id, Names = project + "-web-1", Image = image, State = "running", Status = status,
                Labels = $"com.docker.compose.project={project},com.docker.compose.service=web{labels}", Ports = string.Empty,
            });

        public MatDockFactory App { get; }
        public long EnvId { get; }
        public string ReadKey { get; }
        public UpdateCheckCache Cache => App.Services.GetRequiredService<UpdateCheckCache>();

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public UpdatesApiTests(Site site) => _site = site;

    private HttpClient Full => _site.App.Api(MatDockFactory.EngineKey);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Dictionary<string, JsonElement>> OverviewAsync(HttpClient api, string query = "")
        => (await JsonAsync(await api.GetAsync("/api/v1/updates" + query))).EnumerateArray().ToDictionary(r => r.GetProperty("stack").GetString()!);

    /// <summary>Waits for the operation a 202 answered and gives back how it ended.</summary>
    private async Task<JsonElement> EndAsync(HttpResponseMessage accepted)
    {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await JsonAsync(accepted)).GetProperty("operationId").GetString()!;
        await _site.App.Services.GetRequiredService<OperationRegistry>().WaitAsync(id);
        return await JsonAsync(await Full.GetAsync("/api/v1/operations/" + id));
    }

    private static StackUpdatePlan MovedTag => new([new PlannedUpdate("web", "localhost:1/blog:latest", "bbbbbbbbbbbb", PlannedKind.Digest)], null);

    [Fact]
    public async Task The_overview_has_the_managed_and_the_discovered_stacks_and_a_read_key_starts_nothing()
    {
        var reader = _site.App.Api(_site.ReadKey);

        var rows = await OverviewAsync(reader);

        Assert.Equal(("upd-host", true), (rows["shop"].GetProperty("environment").GetString(), rows["shop"].GetProperty("managed").GetBoolean()));
        Assert.False(rows["blog"].GetProperty("managed").GetBoolean()); // running on the host, not deployed by MatDock
        Assert.Equal("off", rows["blog"].GetProperty("mode").GetString());
        Assert.Contains("blog", (await OverviewAsync(reader, $"?environment={_site.EnvId}")).Keys);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/v1/updates/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/v1/updates/apply", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/updates?environment=nowhere")).StatusCode);
    }

    [Fact]
    public async Task A_look_at_all_stacks_is_one_operation_and_the_overview_has_its_result()
    {
        _site.Cache.Clear(_site.EnvId, "shop");
        _site.Cache.Clear(_site.EnvId, "blog");

        var end = await EndAsync(await Full.PostAsync("/api/v1/updates/check", null));

        Assert.Equal("succeeded", end.GetProperty("status").GetString());
        Assert.Equal(0, end.GetProperty("result").GetProperty("pending").GetInt32());
        var output = end.GetProperty("output").GetString()!;
        Assert.Contains("shop on upd-host: up to date.", output);
        Assert.Contains("blog on upd-host: up to date.", output); // the discovered one in the same run

        var rows = await OverviewAsync(Full);
        Assert.All(new[] { "shop", "blog" }, name =>
        {
            Assert.Equal(JsonValueKind.String, rows[name].GetProperty("checkedAt").ValueKind);
            Assert.Equal(0, rows[name].GetProperty("updates").GetArrayLength());
        });
        Assert.Empty(await OverviewAsync(Full, "?pending=true"));
    }

    [Fact]
    public async Task Update_all_takes_what_the_last_look_found_and_leaves_out_what_matdock_cannot_update()
    {
        var none = new StackUpdatePlan([], null);
        _site.Cache.SetPlan(_site.EnvId, "shop", none);
        _site.Cache.SetPlan(_site.EnvId, "blog", none);
        _site.Cache.SetBlocked(_site.EnvId, "blog", null);

        var nothing = await Full.PostAsync("/api/v1/updates/apply", null);
        Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        Assert.Equal(0, (await JsonAsync(nothing)).GetProperty("stacks").GetInt32());

        // The last look found a moved tag in the discovered stack: it is in the overview and in the run.
        _site.Cache.SetPlan(_site.EnvId, "blog", MovedTag);
        var pending = (await OverviewAsync(Full, "?pending=true"))["blog"];
        Assert.Equal(("web", "digest"), (pending.GetProperty("updates")[0].GetProperty("service").GetString(), pending.GetProperty("updates")[0].GetProperty("kind").GetString()));

        var accepted = await Full.PostAsync("/api/v1/updates/apply", null);
        var end = await EndAsync(accepted);
        Assert.Equal("updates", end.GetProperty("kind").GetString());
        Assert.Equal((0, 0), (end.GetProperty("result").GetProperty("updated").GetInt32(), end.GetProperty("result").GetProperty("failed").GetInt32())); // looked at again: the tag did not move
        Assert.Contains("blog on upd-host", end.GetProperty("output").GetString());

        // One MatDock could not update the last time is said with the reason and not tried again by "all".
        _site.Cache.SetPlan(_site.EnvId, "blog", MovedTag);
        _site.Cache.SetBlocked(_site.EnvId, "blog", "Its compose files are not on the host.");
        try
        {
            Assert.Equal("Its compose files are not on the host.", (await OverviewAsync(Full))["blog"].GetProperty("blocked").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Full.PostAsync("/api/v1/updates/apply", null)).StatusCode);
        }
        finally
        {
            _site.Cache.SetBlocked(_site.EnvId, "blog", null);
            _site.Cache.SetPlan(_site.EnvId, "blog", none);
        }
    }

    [Fact]
    public async Task A_discovered_stack_is_updated_by_its_action_and_cannot_do_what_only_a_managed_one_can()
    {
        var end = await EndAsync(await Full.PostAsync("/api/v1/stacks/blog/actions/update", null));

        Assert.Equal(("update", "blog", "succeeded"), (end.GetProperty("kind").GetString(), end.GetProperty("stack").GetString(), end.GetProperty("status").GetString()));
        Assert.False(end.GetProperty("result").GetProperty("changed").GetBoolean()); // the registry did not move the tag
        Assert.Contains("blog on upd-host: up to date.", end.GetProperty("output").GetString());

        var pull = await Full.PostAsync("/api/v1/stacks/blog/actions/pull", null);
        Assert.Equal(HttpStatusCode.Conflict, pull.StatusCode);
        Assert.Contains("only start, stop, restart and update are possible", await pull.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Full.PostAsync("/api/v1/stacks/nothing/actions/update", null)).StatusCode);
    }

    [Fact]
    public async Task The_update_of_a_managed_stack_follows_its_rule_with_the_health_check_and_the_rollback()
    {
        var hosts = _site.App.Hosts;
        var stackId = _site.App.Db(db => db.Stacks.Single(s => s.Name == "shop").Id);
        bool Seen(string fragment)
        {
            lock (hosts.Commands) { return hosts.Commands.Any(c => c.Contains(fragment, StringComparison.Ordinal)); }
        }

        // The update brings up a new container that is not healthy.
        hosts.Respond = command =>
        {
            var updated = Seen("up -d");
            if (command.Contains(" ps -a", StringComparison.Ordinal))
            {
                var others = hosts.Containers.Where(c => !c.Contains("project=shop", StringComparison.Ordinal));
                return string.Join("\n", others.Prepend(Site.Container("shop", "localhost:1/app:1.2.0", id: updated ? "00000002" : "00000001",
                    status: updated ? "Up 2 seconds (unhealthy)" : "Up 3 hours")));
            }

            return command.Contains("inspect --format", StringComparison.Ordinal)
                ? $"web localhost:1/app:1.2.0 sha256:{(updated ? "bbbbbbbbbbbb" : "aaaaaaaaaaaa")}\n"
                : null;
        };
        var poll = UpdateService.GatePoll;
        UpdateService.GatePoll = TimeSpan.FromMilliseconds(5);
        try
        {
            lock (hosts.Commands) { hosts.Commands.Clear(); }
            var end = await EndAsync(await Full.PostAsync("/api/v1/stacks/shop/actions/update", null));

            // The default rule rolls back: the old image was kept under a name of its own and put back.
            Assert.Equal("failed", end.GetProperty("status").GetString());
            Assert.Contains("shop-web-1 is unhealthy", end.GetProperty("error").GetString());
            Assert.Equal((false, true), (end.GetProperty("result").GetProperty("changed").GetBoolean(), end.GetProperty("result").GetProperty("rolledBack").GetBoolean()));
            Assert.True(Seen("image tag 'sha256:aaaaaaaaaaaa' 'localhost:1/app:matdock-rollback-1.2.0'"));
            Assert.Contains("Rolling back", end.GetProperty("output").GetString());

            // A rule without the rollback: the same update as before, no health check.
            var rule = await Full.PutAsync($"/api/v1/update-rules/stack/{stackId}", new StringContent("""{"rollback":false}"""));
            Assert.Equal(HttpStatusCode.OK, rule.StatusCode);
            lock (hosts.Commands) { hosts.Commands.Clear(); }
            end = await EndAsync(await Full.PostAsync("/api/v1/stacks/shop/actions/update", null));

            Assert.Equal("succeeded", end.GetProperty("status").GetString());
            Assert.Equal((true, false), (end.GetProperty("result").GetProperty("changed").GetBoolean(), end.GetProperty("result").GetProperty("rolledBack").GetBoolean()));
            Assert.False(Seen("matdock-rollback"));
            Assert.DoesNotContain("Bundle backup", end.GetProperty("output").GetString()); // the backup is the choice of the caller (backupFirst), whatever the rule says
        }
        finally
        {
            UpdateService.GatePoll = poll;
            hosts.Respond = null;
            await Full.PutAsync($"/api/v1/update-rules/stack/{stackId}", new StringContent("{}"));
        }
    }

    [Fact]
    public async Task The_description_lists_the_update_endpoints()
    {
        var json = await (await _site.App.Browser().GetAsync("/api/v1/openapi.json")).Content.ReadAsStringAsync();

        Assert.Contains("\"/api/v1/updates\"", json);
        Assert.Contains("/api/v1/updates/check", json);
        Assert.Contains("/api/v1/updates/apply", json);
    }
}
