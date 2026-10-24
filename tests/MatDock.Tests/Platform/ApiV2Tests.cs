using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// The API beyond the stacks: environments, containers, images, volumes, networks, events and update rules; named keys (read or everything, with an
/// expiry, revocable) next to the engine key; the OpenAPI description.
/// </summary>
public sealed class ApiV2Tests : IClassFixture<ApiV2Tests.Site>
{
    private const string Inspect = """[{"Id":"webid0000001","Name":"/web","Created":"2026-10-20T10:00:00Z","Config":{"Image":"nginx:1.27","Env":["DB_PASSWORD=hunter2","TZ=UTC"],"Labels":{}},"State":{"Status":"running","Running":true,"ExitCode":0},"HostConfig":{"RestartPolicy":{"Name":"always"}},"NetworkSettings":{"Networks":{}},"Mounts":[]}]""";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("api-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new { ID = "webid0000001", Names = "web", Image = "nginx:1.27", State = "running", Status = "Up 1 hour", Labels = string.Empty, Ports = string.Empty }));
            App.Hosts.Respond = command => command.Contains(" inspect --type container ", StringComparison.Ordinal) ? Inspect : null;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public ApiV2Tests(Site site) => _site = site;

    private string Key(ApiKeyScope scope, DateTime? expires = null)
    {
        using var scopeServices = _site.App.Services.CreateScope();
        var (_, _, key) = scopeServices.ServiceProvider.GetRequiredService<ApiKeyService>()
            .CreateAsync("key-" + Guid.NewGuid().ToString("N")[..6], scope, expires).GetAwaiter().GetResult();
        return key!;
    }

    private HttpClient Bearer(string key)
    {
        var client = _site.App.Browser();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private string Env => $"environment={_site.EnvId}";

    [Fact]
    public async Task A_read_key_reads_and_may_not_change_anything()
    {
        var reader = _site.App.Api(Key(ApiKeyScope.Read));

        var containers = await reader.GetAsync($"/api/v1/containers?{Env}");
        Assert.Equal(HttpStatusCode.OK, containers.StatusCode);
        Assert.Contains("\"name\":\"web\"", await containers.Content.ReadAsStringAsync());

        _site.App.Hosts.Commands.Clear();
        var stop = await reader.PostAsync($"/api/v1/containers/web/actions/stop?{Env}", null);
        Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        Assert.Contains("may only read", await stop.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" stop ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_key_acts_and_the_audit_names_it()
    {
        var writer = Bearer(Key(ApiKeyScope.Full));
        _site.App.Hosts.Commands.Clear();

        var stop = await writer.PostAsync($"/api/v1/containers/web/actions/stop?{Env}", null);

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" stop 'web'", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.PostAsync($"/api/v1/containers/web/actions/explode?{Env}", null)).StatusCode);
        Assert.True(_site.App.Db(db => db.ActivityEntries.Any(a => a.Actor.StartsWith("API key key-") && a.Action.Contains("/actions/stop"))));
    }

    [Fact]
    public async Task An_expired_or_revoked_key_no_longer_works()
    {
        var expiring = Key(ApiKeyScope.Read, DateTime.UtcNow.AddSeconds(1));
        await Task.Delay(1200);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _site.App.Api(expiring).GetAsync("/api/v1/status")).StatusCode);

        var revoked = Key(ApiKeyScope.Read);
        Assert.Equal(HttpStatusCode.OK, (await _site.App.Api(revoked).GetAsync("/api/v1/status")).StatusCode);
        using (var scope = _site.App.Services.CreateScope())
        {
            var hash = ApiKeyService.Hash(revoked);
            var id = _site.App.Db(db => db.ApiKeys.Single(k => k.KeyHash == hash).Id);
            Assert.True(await scope.ServiceProvider.GetRequiredService<ApiKeyService>().RevokeAsync(id));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _site.App.Api(revoked).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _site.App.Api("mdk_not-a-key").GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task The_engine_objects_are_listed()
    {
        var api = _site.App.Api(MatDockFactory.EngineKey);

        var environments = await (await api.GetAsync("/api/v1/environments")).Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"api-host\"", environments);
        Assert.Contains("\"type\":\"local\"", environments);

        var container = await api.GetAsync($"/api/v1/containers/web?{Env}");
        var body = await container.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, container.StatusCode);
        Assert.Contains("\"restartPolicy\":\"always\"", body);
        Assert.DoesNotContain("hunter2", body);

        foreach (var path in new[] { "images", "volumes", "networks", "events" })
        {
            Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/v1/{path}?{Env}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await api.DeleteAsync($"/api/v1/images?{Env}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/v1/containers?environment=nowhere")).StatusCode);
    }

    [Fact]
    public async Task The_update_rules_are_read_and_set()
    {
        var api = _site.App.Api(MatDockFactory.EngineKey);
        var put = await api.PutAsync($"/api/v1/update-rules/environment/{_site.EnvId}",
            new StringContent("""{"mode":"notify","strategy":"minor","checkEveryHours":12}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var rule = await (await api.GetAsync($"/api/v1/update-rules/environment/{_site.EnvId}")).Content.ReadAsStringAsync();
        Assert.Contains("\"mode\":\"notify\"", rule);
        Assert.Contains("\"strategy\":\"minor\"", rule);
        Assert.Contains("\"checkEveryHours\":12", rule);
        Assert.Contains($"\"id\":{_site.EnvId}", await (await api.GetAsync("/api/v1/update-rules")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/v1/update-rules/global/5", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/v1/update-rules/global/0", new StringContent("""{"checkEveryHours":500}"""))).StatusCode);
    }

    [Fact]
    public async Task The_description_lists_the_endpoints()
    {
        var response = await _site.App.Browser().GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"title\": \"MatDock API\"", json);
        Assert.Contains("/api/v1/containers/{name}/actions/{action}", json);
        Assert.Contains("/api/v1/update-rules", json);
        Assert.DoesNotContain("/api/search", json);
    }

    [Fact]
    public async Task The_page_shows_a_new_key_once()
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        var page = await (await browser.GetAsync("/Settings/ApiKeys")).Content.ReadAsStringAsync();

        var created = await browser.PostAsync("/Settings/ApiKeys?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page), ["name"] = "Grafana", ["scope"] = "Read",
        }));
        var html = await created.Content.ReadAsStringAsync();
        var key = System.Text.RegularExpressions.Regex.Match(html, "value=\"(mdk_[A-Za-z0-9_-]+)\"").Groups[1].Value;
        Assert.StartsWith("mdk_", key);

        var again = await (await browser.GetAsync("/Settings/ApiKeys")).Content.ReadAsStringAsync();
        Assert.Contains("Grafana", again);
        Assert.Contains(key[..12] + "…", again);
        Assert.DoesNotContain(key, again);
        Assert.Equal(HttpStatusCode.OK, (await _site.App.Api(key).GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task A_named_key_alone_opens_the_api()
    {
        using var site = new NoKeyFactory();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await site.Api("anything").GetAsync("/api/v1/status")).StatusCode);

        using var scope = site.Services.CreateScope();
        var (_, _, key) = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("only", ApiKeyScope.Read, null);
        Assert.Equal(HttpStatusCode.OK, (await site.Api(key!).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("wrong").GetAsync("/api/v1/status")).StatusCode);
    }
}
