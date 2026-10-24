using System.Net;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using MatDock.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// The alerts: the channels (Discord, Slack, Teams, Telegram, ntfy, Gotify) with the request each wants and the secret kept encrypted, the limits of the
/// hosts (once over, once back), and the restart loops of containers.
/// </summary>
public sealed class AlertingTests
{
    private static NotificationChannel Channel(NotificationChannelType type, string? url = null, string? target = null)
        => new() { Name = type.ToString(), Type = type, Url = url, Target = target };

    private static async Task<JsonElement> JsonOf(HttpRequestMessage request)
        => JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Each_channel_gets_the_request_it_wants()
    {
        var discord = ChannelMessages.Build(Channel(NotificationChannelType.Discord), "https://discord.com/api/webhooks/1/abc", "Disk full", "Disk of prod at 95 %.");
        Assert.Equal("https://discord.com/api/webhooks/1/abc", discord.RequestUri!.ToString());
        Assert.Equal("**Disk full**\nDisk of prod at 95 %.", (await JsonOf(discord)).GetProperty("content").GetString());

        var slack = ChannelMessages.Build(Channel(NotificationChannelType.Slack), "https://hooks.slack.com/services/x", "S", "B");
        Assert.Equal("*S*\nB", (await JsonOf(slack)).GetProperty("text").GetString());

        var teams = await JsonOf(ChannelMessages.Build(Channel(NotificationChannelType.Teams), "https://example.webhook.office.com/x", "S", "B"));
        var card = teams.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("contentType").GetString());
        Assert.Equal("S", card.GetProperty("content").GetProperty("body")[0].GetProperty("text").GetString());

        var telegram = ChannelMessages.Build(Channel(NotificationChannelType.Telegram, target: "-100123"), "123:ABC", "S", "B");
        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", telegram.RequestUri!.ToString());
        Assert.Equal("-100123", (await JsonOf(telegram)).GetProperty("chat_id").GetString());

        var ntfy = ChannelMessages.Build(Channel(NotificationChannelType.Ntfy, target: "matdock"), "tk_secret", "S", "B");
        Assert.Equal("https://ntfy.sh/", ntfy.RequestUri!.ToString());
        Assert.Equal("Bearer tk_secret", ntfy.Headers.Authorization!.ToString());
        Assert.Equal(("matdock", "S"), ((await JsonOf(ntfy)).GetProperty("topic").GetString(), (await JsonOf(ntfy)).GetProperty("title").GetString()));
        Assert.Null(ChannelMessages.Build(Channel(NotificationChannelType.Ntfy, "https://ntfy.example.com/", "t"), null, "S", "B").Headers.Authorization);

        var gotify = ChannelMessages.Build(Channel(NotificationChannelType.Gotify, "https://gotify.example.com/"), "AppToken", "S", "B");
        Assert.Equal("https://gotify.example.com/message", gotify.RequestUri!.ToString());
        Assert.Equal("AppToken", gotify.Headers.GetValues("X-Gotify-Key").Single());

        var long_ = await JsonOf(ChannelMessages.Build(Channel(NotificationChannelType.Discord), "https://d/x", "S", new string('x', 5000)));
        Assert.Equal(2000, long_.GetProperty("content").GetString()!.Length);
    }

    [Theory]
    [InlineData(NotificationChannelType.Discord, null, null, null, "address of the webhook")]
    [InlineData(NotificationChannelType.Slack, "http://hooks.slack.com/x", null, null, "https://")]
    [InlineData(NotificationChannelType.Telegram, "123:ABC", null, "x y", "chat id")]
    [InlineData(NotificationChannelType.Ntfy, null, null, "bad topic!", "topic")]
    [InlineData(NotificationChannelType.Gotify, "token", null, null, "Gotify server")]
    public void A_channel_without_what_it_needs_is_refused(NotificationChannelType type, string? secret, string? url, string? target, string expected)
    {
        var problem = ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = type, Secret = secret, Url = url, Target = target }, hasSecret: false);
        Assert.Contains(expected, problem);
        Assert.Null(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = NotificationChannelType.Ntfy, Target = "matdock" }, false));
        Assert.Null(ChannelMessages.Problem(new NotificationChannelInput { Name = "x", Type = NotificationChannelType.Discord }, hasSecret: true));
    }

    [Fact]
    public async Task The_secret_is_stored_encrypted_kept_when_blank_and_every_enabled_channel_gets_the_message()
    {
        using var db = new TestDatabase();
        var http = new RecordingHttp();
        var service = new NotificationChannelService(db.Context, new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()), http, NullLogger<NotificationChannelService>.Instance);

        var (ok, _, id) = await service.SaveAsync(null, new NotificationChannelInput { Name = "Ops", Type = NotificationChannelType.Discord, Secret = "https://discord.com/api/webhooks/1/secret" });
        Assert.True(ok);
        await service.SaveAsync(null, new NotificationChannelInput { Name = "Off", Type = NotificationChannelType.Slack, Secret = "https://hooks.slack.com/x", Enabled = false });

        var stored = db.NewContext().NotificationChannels.Single(c => c.Id == id);
        Assert.DoesNotContain("secret", stored.EncryptedSecret);

        Assert.True((await service.SaveAsync(id, new NotificationChannelInput { Name = "Ops team", Type = NotificationChannelType.Discord })).Ok);
        Assert.Equal(stored.EncryptedSecret, db.NewContext().NotificationChannels.Single(c => c.Id == id).EncryptedSecret);

        var errors = await service.SendAllAsync("Subject", "Body");
        Assert.Empty(errors);
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://discord.com/api/webhooks/1/secret", request.Url);

        http.Status = HttpStatusCode.NotFound;
        var failed = await service.SendAllAsync("Subject", "Body");
        Assert.Equal("Ops team: HTTP 404", Assert.Single(failed));
    }

    [Fact]
    public void A_host_limit_is_said_once_when_crossed_and_once_when_back()
    {
        var watch = new HostLimitWatch();
        var env = new DockerEnvironment { Id = 1, Name = "prod" };
        var limits = new HostLimits(Cpu: 80, Mem: null, Disk: 90);
        HostStats Stats(double disk, double load = 0.5) => new() { Cores = 1, Load1 = load, DiskTotalBytes = 100, DiskUsedBytes = (long)disk, MemTotalBytes = 100, MemUsedBytes = 10 };

        Assert.Empty(watch.Check(env, Stats(50), limits));
        Assert.Equal(["Disk of prod at 95 % (limit 90 %)."], watch.Check(env, Stats(95), limits));
        Assert.Empty(watch.Check(env, Stats(96), limits));
        Assert.Empty(watch.Check(env, Stats(89), limits)); // inside the hysteresis
        Assert.Equal(["Disk of prod back at 85 % (limit 90 %)."], watch.Check(env, Stats(85), limits));

        // CPU needs two readings in a row.
        Assert.Empty(watch.Check(env, Stats(50, load: 0.95), limits));
        Assert.Empty(watch.Check(env, Stats(50, load: 0.5), limits));
        Assert.Empty(watch.Check(env, Stats(50, load: 0.95), limits));
        Assert.Equal(["CPU of prod at 95 % (limit 80 %)."], watch.Check(env, Stats(50, load: 0.95), limits));

        // Nothing read: no message, no state.
        Assert.Empty(watch.Check(env, new HostStats(), limits));
    }

    [Fact]
    public void The_limits_of_a_host_come_from_it_or_the_settings()
    {
        var settings = new NotificationSettings { HostCpuLimit = 85, HostMemLimit = null, HostDiskLimit = 90 };
        Assert.Equal(new HostLimits(85, null, 90), HostLimits.Of(settings, new DockerEnvironment()));
        Assert.Equal(new HostLimits(null, 70, 95), HostLimits.Of(settings, new DockerEnvironment { AlertCpuPercent = 0, AlertMemPercent = 70, AlertDiskPercent = 95 }));
    }

    [Fact]
    public void A_restart_loop_is_said_once_per_window()
    {
        var watch = new RestartLoopWatch();
        var env = new DockerEnvironment { Id = 1, Name = "prod" };
        var t0 = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
        ContainerStart Start(int minute) => new("abc", "worker", t0.AddMinutes(minute));
        var window = TimeSpan.FromMinutes(10);

        Assert.Empty(watch.Add(env, [Start(0), Start(1)], 3, window, t0.AddMinutes(1)));
        Assert.Equal(["Container worker on prod started 3 times in 10 minutes (a restart loop)."], watch.Add(env, [Start(2)], 3, window, t0.AddMinutes(2)));
        Assert.Empty(watch.Add(env, [Start(3), Start(4)], 3, window, t0.AddMinutes(4)));
        Assert.Single(watch.Add(env, [Start(13), Start(14)], 3, window, t0.AddMinutes(14)));

        // Starts spread over more than the window are no loop; 0 starts = not watched.
        var calm = new RestartLoopWatch();
        Assert.Empty(calm.Add(env, [Start(0)], 3, window, t0));
        Assert.Empty(calm.Add(env, [Start(11)], 3, window, t0.AddMinutes(11)));
        Assert.Empty(calm.Add(env, [Start(22)], 3, window, t0.AddMinutes(22)));
        Assert.Empty(new RestartLoopWatch().Add(env, [Start(0), Start(0), Start(0)], 0, window, t0));
    }

    [Fact]
    public void The_starts_are_read_from_the_events()
    {
        var output = """
            {"Type":"container","Action":"start","Actor":{"ID":"abc","Attributes":{"name":"worker"}},"timeNano":1761386400000000000}
            {"Type":"container","Action":"die","Actor":{"ID":"abc","Attributes":{"name":"worker","exitCode":"1"}},"timeNano":1761386401000000000}
            """;
        var start = Assert.Single(ContainerAlerts.Starts(output));
        Assert.Equal(("abc", "worker"), (start.Id, start.Name));
    }

    private sealed class RecordingHttp : IHttpClientFactory
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<(string Url, string Body)> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(RecordingHttp owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add((request.RequestUri!.ToString(), request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(owner.Status);
            }
        }
    }
}
