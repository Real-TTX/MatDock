using System.Net;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Images;
using MatDock.Core.Notifications;
using MatDock.Core.Schedules;
using MatDock.Core.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// The rest of the Dockhand comparison: a compose file from a running container (secrets as variables), public addresses from proxy labels, more
/// notification services, stacks started and stopped by a schedule, and the scan of an image to download as CSV, JSON or SARIF.
/// </summary>
public sealed class DockhandRestTests : IClassFixture<DockhandRestTests.Site>
{
    private const string Inspect = """
        [{
          "Id": "appid0000000000000000000000000000000000000000000000000000000000001",
          "Name": "/app",
          "Image": "sha256:img",
          "State": { "Status": "running", "Running": true },
          "Config": { "Hostname": "appid0000000", "Image": "ghcr.io/acme/app:1.2", "User": "", "WorkingDir": "/srv",
                      "Env": ["PATH=/usr/local/bin:/usr/bin", "DB_PASSWORD=hunter2", "MODE=prod", "PRICE=5$"],
                      "Cmd": ["serve", "--port", "8080"], "Entrypoint": ["/entry.sh"],
                      "Labels": { "org.opencontainers.image.title": "app", "team": "core" } },
          "HostConfig": { "RestartPolicy": { "Name": "unless-stopped" }, "NetworkMode": "backend",
                          "PortBindings": { "8080/tcp": [ { "HostIp": "", "HostPort": "18080" } ] } },
          "Mounts": [ { "Type": "volume", "Name": "app-data", "Source": "/var/lib/docker/volumes/app-data/_data", "Destination": "/data", "RW": true },
                      { "Type": "bind", "Source": "/etc/app", "Destination": "/config", "RW": false } ],
          "NetworkSettings": { "Networks": { "backend": { "IPAddress": "172.30.0.9", "IPAMConfig": { "IPv4Address": "172.30.0.9" }, "Aliases": ["app", "api"] } } }
        }]
        """;

    private const string ImageConfigJson = """
        {"Env":["PATH=/usr/local/bin:/usr/bin"],"Labels":{"org.opencontainers.image.title":"app"},"Cmd":["serve"],"Entrypoint":["/entry.sh"],"WorkingDir":"/srv","User":""}
        """;

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("rest-host");
            App.Hosts.Respond = command =>
                command.Contains(" inspect --type container ", StringComparison.Ordinal) ? Inspect
                : command.Contains(" image inspect --format '{{json .Config}}'", StringComparison.Ordinal) ? ImageConfigJson
                : null;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public DockhandRestTests(Site site) => _site = site;

    private async Task<(HttpClient Browser, string Html)> AdminAsync(string page)
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        return (browser, await (await browser.GetAsync(page)).Content.ReadAsStringAsync());
    }

    // ---- compose from a container ----

    [Fact]
    public async Task A_running_container_becomes_a_compose_file_with_its_secrets_as_variables()
    {
        var (browser, html) = await AdminAsync($"/Stacks/FromContainer?envId={_site.EnvId}&id=appid0000001");
        var page = WebUtility.HtmlDecode(html);
        Assert.Contains("image: ghcr.io/acme/app:1.2", page);
        Assert.Contains("restart: unless-stopped", page);
        Assert.Contains("DB_PASSWORD: ${DB_PASSWORD}", page);
        Assert.DoesNotContain("hunter2", page);
        Assert.Contains("PRICE: 5$$", page);              // a literal dollar stays a dollar
        Assert.DoesNotContain("PATH:", page);              // the image's own variable
        Assert.Contains("- 18080:8080", page);
        Assert.Contains("- app-data:/data", page);
        Assert.Contains("ipv4_address: 172.30.0.9", page);
        Assert.Contains("external: true", page);
        Assert.Contains("team: core", page);
        Assert.DoesNotContain("org.opencontainers.image.title", page); // the image's own label

        var create = await browser.PostAsync("/Stacks/FromContainer", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["EnvId"] = _site.EnvId.ToString(),
            ["Id"] = "appid0000001",
            ["Name"] = "app-from-container",
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var stack = _site.App.Db(db => db.Stacks.Single(s => s.Name == "app-from-container"));
        Assert.Equal("hunter2", StackSecrets.Parse(stack.SecretsJson)["DB_PASSWORD"]);
        Assert.Null(ComposeCheck.Problem(stack.ComposeYaml));
        Assert.Empty(ComposeLint.Check(stack.ComposeYaml, defined: ["DB_PASSWORD"]).Where(f => f.Severity == LintSeverity.Error));
    }

    // ---- proxy addresses ----

    [Fact]
    public void The_labels_of_a_reverse_proxy_name_the_public_addresses()
    {
        var urls = ProxyUrls.From(new Dictionary<string, string>
        {
            ["traefik.http.routers.shop.rule"] = "Host(`shop.example.com`) || Host(`www.shop.example.com`)",
            ["traefik.http.routers.shop.entrypoints"] = "websecure",
            ["traefik.http.routers.plain.rule"] = "Host(\"intern.example.com\") && PathPrefix(`/api`)",
            ["traefik.http.routers.plain.entrypoints"] = "web",
            ["caddy"] = "blog.example.com",
            ["caddy_1"] = "*.wild.example.com",
            ["matdock.url"] = "https://app.example.com/",
        });
        Assert.Equal(new[] { "https://app.example.com", "https://blog.example.com", "http://intern.example.com", "https://shop.example.com", "https://www.shop.example.com" }, urls);
        Assert.Empty(ProxyUrls.From(new Dictionary<string, string> { ["matdock.url"] = "javascript:alert(1)", ["traefik.http.routers.x.rule"] = "PathPrefix(`/x`)" }));
    }

    // ---- notifications ----

    [Theory]
    [InlineData(NotificationChannelType.Mattermost, "https://chat.example.com/hooks/abc", null, null, "https://chat.example.com/hooks/abc", "\"text\":")]
    [InlineData(NotificationChannelType.Pushover, "apptoken", "u0123456789012345678901234567x", null, "https://api.pushover.net/1/messages.json", "user=u0123456789012345678901234567x")]
    [InlineData(NotificationChannelType.Apprise, null, "ops", "http://apprise:8000", "http://apprise:8000/notify/ops", "\"title\":")]
    [InlineData(NotificationChannelType.Apprise, "tgram://bot/chat", null, "http://apprise:8000", "http://apprise:8000/notify", "\"urls\":\"tgram://bot/chat\"")]
    [InlineData(NotificationChannelType.Bark, "devicekey", null, null, "https://api.day.app/push", "\"device_key\":\"devicekey\"")]
    [InlineData(NotificationChannelType.Signal, "+491701234567", "+491709999999,group.abc", "http://signal:8080", "http://signal:8080/v2/send", "\"group.abc\"")]
    public async Task The_new_services_get_the_request_they_want(NotificationChannelType type, string? secret, string? target, string? url, string expectedUrl, string expectedBody)
    {
        var channel = new NotificationChannel { Name = "x", Type = type, Target = target, Url = url };
        Assert.Null(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = type, Target = target, Url = url, Secret = secret }, hasSecret: false));
        using var request = ChannelMessages.Build(channel, secret, "Subject", "Body");
        Assert.Equal(expectedUrl, request.RequestUri!.ToString());
        Assert.Contains(expectedBody, await request.Content!.ReadAsStringAsync());
    }

    [Fact]
    public void The_new_services_say_what_is_missing()
    {
        Assert.NotNull(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = NotificationChannelType.Pushover, Secret = "t", Target = "short" }, false));
        Assert.NotNull(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = NotificationChannelType.Apprise, Url = "http://a:8000" }, false));
        Assert.NotNull(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = NotificationChannelType.Signal, Url = "http://s:8080", Secret = "+49" }, false));
        Assert.NotNull(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = NotificationChannelType.Mattermost, Secret = "http://plain" }, false));
    }

    // ---- start and stop by a schedule ----

    [Fact]
    public async Task A_schedule_stops_and_starts_the_named_stacks()
    {
        using (var scope = _site.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<StackService>()
                .CreateAsync(new StackInput { Name = "night-off", EnvironmentId = _site.EnvId, ComposeYaml = "services:\n  web:\n    image: nginx:1.27\n" });
        }

        Assert.Equal(new[] { "a", "b" }, ScheduleService.StackNames("a, b,a"));
        foreach (var (action, verb) in new[] { (ScheduleAction.StopStacks, " stop"), (ScheduleAction.StartStacks, " start") })
        {
            _site.App.Hosts.Commands.Clear();
            using var scope = _site.App.Services.CreateScope();
            var runner = scope.ServiceProvider.GetServices<IScheduleAction>().Single(a => a.Type == action);
            var task = new ScheduledTask { Name = "t", Action = action, EnvironmentId = _site.EnvId, OptionsJson = new ScheduleOptions { BundleStack = "night-off, gone" }.ToJson() };
            var (ok, summary) = await runner.ExecuteAsync(task);
            Assert.False(ok); // "gone" is not there
            Assert.Contains("Not found: gone", summary);
            Assert.Contains(_site.App.Hosts.Commands, c => c.Contains("'night-off'", StringComparison.Ordinal) && c.Contains(verb + " ", StringComparison.Ordinal) || c.Contains("'night-off'", StringComparison.Ordinal) && c.Contains("$ov $ef" + verb, StringComparison.Ordinal));
        }
    }

    // ---- scan export ----

    [Fact]
    public async Task The_scan_of_an_image_downloads_as_csv_json_and_sarif()
    {
        var findings = new[] { new ImageFinding("CVE-2026-1", "CRITICAL", "openssl", "3.0.1", "3.0.9", "=HYPERLINK(\"x\"), bad"), new ImageFinding("GHSA-x", "LOW", "lib", "1", null, null) };
        _site.App.Db(db =>
        {
            db.ImageScans.Add(new ImageScan { EnvironmentId = _site.EnvId, ImageRef = "nginx:1.27", ScannedAt = DateTime.UtcNow, Critical = 1, Low = 1, FindingsJson = JsonSerializer.Serialize(findings) });
            return db.SaveChanges();
        });

        var (browser, _) = await AdminAsync("/");
        var csv = await browser.GetStringAsync($"/Images/Detail?handler=ScanExport&envId={_site.EnvId}&reference=nginx:1.27&format=csv");
        Assert.StartsWith("id,severity,package,installed,fixed,title\n", csv);
        Assert.Contains("CVE-2026-1,CRITICAL,openssl,3.0.1,3.0.9,\"'=HYPERLINK(\"\"x\"\"), bad\"", csv);

        var json = JsonDocument.Parse(await browser.GetStringAsync($"/Images/Detail?handler=ScanExport&envId={_site.EnvId}&reference=nginx:1.27&format=json")).RootElement;
        Assert.Equal(1, json.GetProperty("counts").GetProperty("critical").GetInt32());

        var sarif = JsonDocument.Parse(await browser.GetStringAsync($"/Images/Detail?handler=ScanExport&envId={_site.EnvId}&reference=nginx:1.27&format=sarif")).RootElement;
        Assert.Equal("2.1.0", sarif.GetProperty("version").GetString());
        var results = sarif.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("error", results[0].GetProperty("level").GetString());
        Assert.Equal("note", results[1].GetProperty("level").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Images/Detail?handler=ScanExport&envId={_site.EnvId}&reference=never:scanned&format=csv")).StatusCode);
    }
}
