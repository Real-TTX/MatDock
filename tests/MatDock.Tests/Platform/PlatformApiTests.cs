using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatDock.Core;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Web.Platform;
using MatPlatform;

namespace MatDock.Tests.Platform;

/// <summary>The description of MatDock is valid for the validator of MatPlatform, in both languages and against the sprite of MatUi.</summary>
public sealed class PlatformDefinitionTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
        => Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(DockPlatform.Describe, language), Sprite()));

    [Fact]
    public async Task The_served_definition_is_valid_and_names_the_app()
    {
        var json = await site.CreateClient().GetStringAsync("/api");
        Assert.Empty(MatPlatformValidator.Validate(json, Sprite()));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var app = root.GetProperty("app");
        Assert.Equal(1, root.GetProperty("contract").GetInt32());
        Assert.Equal("matdock", app.GetProperty("id").GetString());
        Assert.Equal("#1d78be", app.GetProperty("accent").GetString());
        Assert.Equal(AppVersion.Informational, app.GetProperty("version").GetString());
        Assert.True(root.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(root.GetProperty("auth").TryGetProperty("identity", out _));
        Assert.Equal(["overview", "settings"], root.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(["admin", "user"], root.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("key").GetString()));
        Assert.True(root.GetProperty("configure").GetArrayLength() <= 10);

        var actions = root.GetProperty("actions").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal(["open", "stacks", "backups", "runBackups", "pruneImages"], actions.Keys);
        Assert.True(actions["pruneImages"].GetProperty("danger").GetBoolean());
        Assert.False(string.IsNullOrEmpty(actions["pruneImages"].GetProperty("confirm").GetString()));
        Assert.False(string.IsNullOrEmpty(actions["runBackups"].GetProperty("confirm").GetString()));
        Assert.Equal("/Stacks", actions["stacks"].GetProperty("url").GetString());
        Assert.Equal("/Backups", actions["backups"].GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_texts_follow_accept_language()
    {
        Assert.Contains("\"Laufende Container\"", await GetAsync("de"));
        Assert.Contains("\"Running containers\"", await GetAsync("en"));

        async Task<string> GetAsync(string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
            request.Headers.AcceptLanguage.ParseAdd(language);
            return await (await site.CreateClient().SendAsync(request)).Content.ReadAsStringAsync();
        }
    }

    [Fact]
    public async Task The_definition_has_an_etag_and_answers_304()
    {
        var client = site.CreateClient();
        var first = await client.GetAsync("/api");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api");
        again.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    internal static IReadOnlySet<string> Sprite()
        => MatPlatformValidator.ReadSprite(Path.Combine(AppContext.BaseDirectory, "Fixtures", "matui-icons.svg"));
}

/// <summary>The endpoints: shapes, status codes, keys, secrets.</summary>
public sealed class PlatformApiTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    private const string SmtpPassword = "smtp-password-never-shown";

    [Fact]
    public async Task Health_is_anonymous_and_ok()
    {
        var response = await site.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("matdock", doc.RootElement.GetProperty("app").GetString());
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("ok", checks["database"]);
        Assert.Equal("ok", checks["environments"]);
    }

    [Theory]
    [InlineData("GET", "/api/state")]
    [InlineData("GET", "/api/state/containersRunning")]
    [InlineData("GET", "/api/configure")]
    [InlineData("PUT", "/api/configure")]
    [InlineData("POST", "/api/actions/pruneImages")]
    public async Task Without_the_key_it_is_401(string method, string path)
    {
        var prunes = site.Connection.ImagePrunes;
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : JsonContent.Create(new { values = new { } }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.CreateClient().SendAsync(request)).StatusCode);

        using var wrong = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : JsonContent.Create(new { values = new { } }) };
        wrong.Headers.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.CreateClient().SendAsync(wrong)).StatusCode);
        Assert.Equal(prunes, site.Connection.ImagePrunes);
    }

    [Fact]
    public async Task State_has_every_tile_with_real_values()
    {
        // An app of its own: the tiles count every host, and the other tests add hosts.
        using var app = new MatDockFactory();
        app.AddLocalEnvironment("state-host");
        app.Db(db =>
        {
            db.Environments.Add(new DockerEnvironment { Name = "nas", Host = "nas", Username = "x", Status = EnvironmentStatus.Offline, IsEnabled = true });
            db.ScheduledTasks.Add(new ScheduledTask
            {
                Name = "Nightly", Action = ScheduleAction.BundleBackup, Trigger = ScheduleTrigger.Cron, Cron = "0 3 * * *", Enabled = true,
                LastRunAt = DateTime.UtcNow.AddHours(-2), LastRunOk = false, LastStatus = "Error: target unreachable", NextRunAt = DateTime.UtcNow.AddHours(5),
            });
            db.StackBackups.Add(new StackBackup { EnvironmentId = 1, EnvironmentName = "local", StackName = "web", Status = StackBackupStatus.Succeeded, Message = "2 volumes" });
            db.VolumeBackups.Add(new VolumeBackup { VolumeName = "web_data", FileName = "a.tar.gz", SizeBytes = 1000 });
            db.VolumeBackups.Add(new VolumeBackup { VolumeName = "web_db", FileName = "b.tar.gz", SizeBytes = 500 });
            return db.SaveChanges();
        });
        app.Hosts.Containers.AddRange(
        [
            Container("a", "running", "Up 3 hours", "web"),
            Container("b", "running", "Up 3 hours (unhealthy)", "web"),
            Container("c", "exited", "Exited (0) 2 days ago", "db"),
            Container("d", "running", "Up 1 minute", null),
        ]);
        app.Connection.Images = [new DockerImage { Id = "1", Repository = "nginx", Tag = "1" }, new DockerImage { Id = "2", Repository = "<none>", Tag = "<none>" }];
        app.Connection.Stats = new HostStats { DiskTotalBytes = 100_000, DiskUsedBytes = 40_000 };

        using var doc = JsonDocument.Parse(await app.Api().GetStringAsync("/api/state"));
        var values = doc.RootElement.GetProperty("values");
        foreach (var tile in MatPlatformBuilder.Build(DockPlatform.Describe, "en").State)
        {
            Assert.True(values.TryGetProperty(tile.Key, out _), tile.Key);
        }

        int Number(string key) => values.GetProperty(key).GetProperty("value").GetInt32();
        Assert.Equal(2, Number("environments"));
        Assert.Equal(1, Number("environmentsUnreachable"));
        Assert.Equal("warn", values.GetProperty("environmentsUnreachable").GetProperty("level").GetString());
        Assert.Equal(2, Number("stacks"));
        Assert.Equal(3, Number("containersRunning"));
        Assert.Equal(1, Number("containersStopped"));
        Assert.Equal(1, Number("containersUnhealthy"));
        Assert.Equal("error", values.GetProperty("containersUnhealthy").GetProperty("level").GetString());
        Assert.Equal(2, Number("images"));
        Assert.Equal(40_000, Number("hostDisk"));
        Assert.Equal(100_000, Number("hostDiskTotal"));
        Assert.Equal(1500, Number("backupSize"));
        Assert.Equal(1, Number("backupFailures24h"));
        Assert.True(DateTimeOffset.TryParse(values.GetProperty("nextBackup").GetProperty("value").GetString(), out _));
        Assert.True(DateTimeOffset.TryParse(values.GetProperty("lastBackupAt").GetProperty("value").GetString(), out _));

        // The newest run is the stack backup that just succeeded.
        var last = values.GetProperty("lastBackup");
        Assert.Equal("ok", last.GetProperty("value").GetString());
        Assert.Contains("web", last.GetProperty("text").GetString());
    }

    [Fact]
    public async Task One_state_value_by_key_and_404_for_an_unknown_key()
    {
        using var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state/containersRunning"));
        Assert.Equal("containersRunning", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("value").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await site.Api().GetAsync("/api/state/nope")).StatusCode);
    }

    [Fact]
    public async Task Configure_saves_the_notification_settings_and_never_sends_the_password()
    {
        var put = await Put(new
        {
            notifyOnFailure = true,
            smtpEnabled = true,
            smtpHost = "mail.example.com",
            smtpPort = 2525,
            smtpUsername = "dock",
            smtpPassword = SmtpPassword,
            smtpFrom = "dock@example.com",
            smtpTo = "ops@example.com; admin@example.com",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SmtpPassword, body);

        var get = await site.Api().GetStringAsync("/api/configure");
        Assert.DoesNotContain(SmtpPassword, get);
        using var doc = JsonDocument.Parse(get);
        var values = doc.RootElement.GetProperty("values");
        Assert.True(values.GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
        Assert.Equal("mail.example.com", values.GetProperty("smtpHost").GetString());
        Assert.Equal(2525, values.GetProperty("smtpPort").GetInt32());
        Assert.True(values.GetProperty("smtpEnabled").GetBoolean());

        // The settings page reads the same row; the password is kept encrypted, never in plain text.
        var stored = site.Db(db => db.NotificationSettings.Single());
        Assert.Equal("mail.example.com", stored.SmtpHost);
        Assert.NotEqual(SmtpPassword, stored.EncryptedSmtpPassword);

        // A change without the password keeps it; "" clears it.
        Assert.Equal(HttpStatusCode.OK, (await Put(new { smtpHost = "mail2.example.com" })).StatusCode);
        Assert.NotNull(site.Db(db => db.NotificationSettings.Single().EncryptedSmtpPassword));
        Assert.Equal(HttpStatusCode.OK, (await Put(new { smtpPassword = "" })).StatusCode);
        Assert.Null(site.Db(db => db.NotificationSettings.Single().EncryptedSmtpPassword));
        using var cleared = JsonDocument.Parse(await site.Api().GetStringAsync("/api/configure"));
        Assert.False(cleared.RootElement.GetProperty("values").GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task A_wrong_value_is_400_per_key_and_nothing_is_saved()
    {
        var before = site.Db(db => db.NotificationSettings.Select(s => s.WebhookUrl).FirstOrDefault());

        var response = await Put(new { webhookEnabled = true, webhookUrl = "ftp://nope", smtpPort = 70000 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("smtpPort", out _) || errors.TryGetProperty("webhookUrl", out _));
        Assert.Equal(before, site.Db(db => db.NotificationSettings.Select(s => s.WebhookUrl).FirstOrDefault()));

        // A field that is required when it is shown: e-mail on without a host.
        var missing = await Put(new { smtpEnabled = true, smtpHost = "" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var unknown = await Put(new { nope = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("nope", await unknown.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await site.Api().PutAsync("/api/configure", new StringContent("not json", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Prune_images_removes_dangling_images_only()
    {
        site.AddLocalEnvironment("prune-host");
        site.Connection.ImagePrune = new PruneResult(true, 3, string.Empty, "1.2GB");

        var response = await site.Api().PostAsync("/api/actions/pruneImages", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("3", doc.RootElement.GetProperty("message").GetString());
        Assert.False(site.Connection.LastPruneAll);
    }

    [Fact]
    public async Task Run_backups_without_a_backup_schedule_is_409()
    {
        site.Db(db =>
        {
            db.ScheduledTasks.RemoveRange(db.ScheduledTasks);
            return db.SaveChanges();
        });

        var response = await site.Api().PostAsync("/api/actions/runBackups", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("open")] // a link action is not posted
    public async Task An_unknown_action_is_404(string id)
        => Assert.Equal(HttpStatusCode.NotFound, (await site.Api().PostAsync("/api/actions/" + id, null)).StatusCode);

    private Task<HttpResponseMessage> Put(object values) => site.Api().PutAsJsonAsync("/api/configure", new { values });

    private static string Container(string id, string state, string status, string? project)
        => JsonSerializer.Serialize(new
        {
            ID = id,
            Names = "c-" + id,
            Image = "nginx",
            State = state,
            Status = status,
            Labels = project is null ? string.Empty : $"com.docker.compose.project={project},com.docker.compose.service=app",
        });
}

/// <summary>The key: <c>MAT_API_KEY</c>, else the engine key of /api/v1; without both the protected part is closed.</summary>
public sealed class PlatformKeyTests
{
    [Fact]
    public async Task With_both_keys_the_platform_takes_mat_api_key_only()
    {
        using var site = new MatDockFactory();
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatDockFactory.ApiKey).GetAsync("/api/state/stacks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api(MatDockFactory.EngineKey).GetAsync("/api/state/stacks")).StatusCode);
    }

    [Fact]
    public async Task Without_mat_api_key_the_engine_key_opens_the_platform()
    {
        using var site = new EngineKeyOnlyFactory();
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatDockFactory.EngineKey).GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("wrong").GetAsync("/api/configure")).StatusCode);

        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.True(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
    }

    [Fact]
    public async Task Without_any_key_the_protected_part_is_401_and_the_definition_says_so()
    {
        using var site = new NoKeyFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("anything").GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.CreateClient().GetAsync("/api/health")).StatusCode);

        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
    }
}

/// <summary>The engine API of Mattiverse (/api/v1) answers exactly as before, next to the platform contract.</summary>
public sealed class EngineApiUnchangedTests
{
    [Fact]
    public async Task Status_answers_as_before_with_the_engine_key()
    {
        using var site = new MatDockFactory();
        var response = await site.Api(MatDockFactory.EngineKey).GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"{{\"ok\":true,\"app\":\"matdock\",\"version\":\"{AppVersion.Informational}\",\"apiVersion\":1,\"environments\":[]}}",
            await response.Content.ReadAsStringAsync());

        // The platform key is not the engine key: /api/v1 keeps its own guard.
        var platformKey = await site.Api(MatDockFactory.ApiKey).GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, platformKey.StatusCode);
        Assert.Equal("{\"error\":\"Invalid or missing X-Api-Key.\"}", await platformKey.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stacks_and_operations_keep_their_routes()
    {
        using var site = new MatDockFactory();
        site.AddLocalEnvironment();
        var api = site.Api(MatDockFactory.EngineKey);

        var stacks = await api.GetAsync("/api/v1/stacks");
        Assert.Equal(HttpStatusCode.OK, stacks.StatusCode);
        Assert.Equal("[]", await stacks.Content.ReadAsStringAsync());

        var operation = await api.GetAsync("/api/v1/operations/unknown");
        Assert.Equal(HttpStatusCode.NotFound, operation.StatusCode);
        Assert.Equal("{\"error\":\"Operation not found (unknown, or finished more than an hour ago).\"}", await operation.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_an_engine_key_v1_is_503_as_before()
    {
        using var site = new NoKeyFactory();
        var response = await site.Api("anything").GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"The MatDock API is disabled: no API key is configured (MatDock__Api__Key).\"}", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>A degraded MatDock answers 200 (the Docker health check must not restart it), with the reason.</summary>
public sealed class PlatformHealthTests
{
    [Fact]
    public async Task An_unreachable_environment_makes_it_degraded_not_down()
    {
        using var site = new MatDockFactory();
        site.Db(db =>
        {
            db.Environments.Add(new DockerEnvironment { Name = "nas", Host = "nas", Username = "x", Status = EnvironmentStatus.Error, IsEnabled = true });
            return db.SaveChanges();
        });

        var response = await site.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("degraded", doc.RootElement.GetProperty("status").GetString());
        var environments = doc.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "environments");
        Assert.Contains("nas", environments.GetProperty("message").GetString());
    }
}
