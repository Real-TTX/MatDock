using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using MatPlatform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>MatDock with the MATSMTP_* variables of Mattiverse (MatDesign docs/PLATFORM.md "Mail through MatSMTP").</summary>
public sealed class MatSmtpFactory : MatDockFactory
{
    public const string Host = "mattiverse-matsmtp-matsmtp";
    public const string Password = "relay-secret-4711";
    public const string From = "matdock@example.org";

    public static readonly IReadOnlyDictionary<string, string> Variables = new Dictionary<string, string>
    {
        [MatSmtpSettings.HostVariable] = Host,
        [MatSmtpSettings.PortVariable] = "2025",
        [MatSmtpSettings.UsernameVariable] = "matdock",
        [MatSmtpSettings.PasswordVariable] = Password,
        [MatSmtpSettings.FromVariable] = From,
        [MatSmtpSettings.TlsVariable] = "starttls",
    };

    protected override IReadOnlyDictionary<string, string> MatSmtp => Variables;
}

public sealed class MailServerTests
{
    private static NotificationSettings Own() => new()
    {
        SmtpEnabled = true, SmtpHost = "smtp.own.example", SmtpPort = 587, SmtpUseTls = true, SmtpUsername = "me", SmtpFrom = "own@example.org", SmtpTo = "admin@example.org",
    };

    [Fact]
    public void Without_a_relay_the_own_settings_are_used_as_before()
    {
        var mail = MailServer.Compose(Own(), "pw", null);
        Assert.Equal(new MailClientSettings("smtp.own.example", 587, MailSecurity.StartTls, "me", "pw", "own@example.org", false), mail);

        var own = Own();
        own.SmtpPort = 465;
        Assert.Equal(MailSecurity.Tls, MailServer.Compose(own, "pw", null).Security);
        own.SmtpUseTls = false;
        own.SmtpUsername = null;
        var plain = MailServer.Compose(own, "pw", null);
        Assert.Equal(MailSecurity.None, plain.Security);
        Assert.Null(plain.Username);
    }

    [Theory]
    [InlineData("none", MailSecurity.None)]
    [InlineData("starttls", MailSecurity.StartTls)]
    [InlineData("tls", MailSecurity.Tls)]
    public void With_matsmtp_all_mail_goes_through_it(string tls, MailSecurity security)
    {
        var variables = MatSmtpFactory.Variables.ToDictionary(v => v.Key, v => (string?)v.Value);
        variables[MatSmtpSettings.TlsVariable] = tls;
        var relay = new MatDock.Web.Platform.MatSmtpRelaySource(MatSmtpSettings.From(new ConfigurationBuilder().AddInMemoryCollection(variables).Build())).Current;

        var mail = MailServer.Compose(Own(), "pw", relay);
        Assert.Equal(new MailClientSettings(MatSmtpFactory.Host, 2025, security, "matdock", MatSmtpFactory.Password, MatSmtpFactory.From, true), mail);
        Assert.DoesNotContain(MatSmtpFactory.Password, relay!.Description);
    }

    [Fact]
    public void A_change_of_the_fixed_fields_is_never_saved()
    {
        var relay = new MailRelay("relay", 2025, MailSecurity.None, null, null, "r@example.org", "…");
        var input = new NotificationSettingsInput { SmtpHost = "evil", SmtpPort = 1, SmtpFrom = "x@y.z", SmtpPassword = "new", SmtpTo = "a@b.c", SmtpEnabled = true };
        var kept = MailServer.KeepFixed(input, Own(), relay);
        Assert.Equal("smtp.own.example", kept.SmtpHost);
        Assert.Equal(587, kept.SmtpPort);
        Assert.Equal("own@example.org", kept.SmtpFrom);
        Assert.Null(kept.SmtpPassword);
        Assert.Equal("a@b.c", kept.SmtpTo);
    }

    [Fact]
    public async Task A_notification_reaches_the_server_through_mailkit()
    {
        using var server = new StubSmtpServer();
        using var db = new TestDatabase();
        var relay = new StaticRelay(new MailRelay("127.0.0.1", server.Port, MailSecurity.None, null, null, "matdock@example.org", "relay"));
        var settings = new NotificationSettingsService(db.Context, new FakeSecretProtector(), relay);
        await settings.UpdateAsync(new NotificationSettingsInput { SmtpEnabled = true, SmtpTo = "admin@example.org", NotifyOnFailure = true });
        var notifications = new NotificationService(settings, new NoHttp(), NullLogger<NotificationService>.Instance);

        var (ok, message) = await notifications.NotifyAsync("MatDock: Container died", "Container db exited with code 1 on local.");

        Assert.True(ok, message);
        var data = await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("MAIL FROM:<matdock@example.org>", data);
        Assert.Contains("RCPT TO:<admin@example.org>", data);
        Assert.Contains("Subject: MatDock: Container died", data);
        Assert.Contains("Container db exited with code 1 on local.", data);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StaticRelay(MailRelay relay) : IMailRelaySource
    {
        public MailRelay? Current => relay;
    }

    /// <summary>Just enough SMTP to take one message (no TLS, no AUTH).</summary>
    private sealed class StubSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public StubSmtpServer()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TaskCompletionSource<string> Received { get; } = new();

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var log = new StringBuilder();
            await writer.WriteLineAsync("220 stub ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                log.AppendLine(line);
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 stub");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 go on");
                    while (await reader.ReadLineAsync() is { } body && body != ".")
                    {
                        log.AppendLine(body);
                    }

                    await writer.WriteLineAsync("250 queued");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 ok");
                }
            }

            Received.TrySetResult(log.ToString());
        }

        public void Dispose() => _listener.Stop();
    }
}

public sealed class MatSmtpSiteTests(MatSmtpFactory site) : IClassFixture<MatSmtpFactory>
{
    private static readonly string[] FixedKeys = ["smtpHost", "smtpPort", "smtpUsername", "smtpPassword", "smtpFrom"];

    [Fact]
    public async Task The_definition_marks_the_server_fields_fixed_and_configure_answers_matsmtp()
    {
        var definition = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api")).RootElement;
        var fields = definition.GetProperty("configure").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        foreach (var key in FixedKeys)
        {
            Assert.True(fields[key].GetProperty("fixed").GetBoolean(), key);
            Assert.Contains(MatSmtpFactory.Host, fields[key].GetProperty("hint").GetString());
        }

        Assert.False(fields["smtpTo"].TryGetProperty("fixed", out _));

        var body = await site.Api().GetStringAsync("/api/configure");
        Assert.DoesNotContain(MatSmtpFactory.Password, body);
        var values = JsonDocument.Parse(body).RootElement.GetProperty("values");
        Assert.Equal(MatSmtpFactory.Host, values.GetProperty("smtpHost").GetString());
        Assert.Equal(2025, values.GetProperty("smtpPort").GetInt32());
        Assert.Equal(MatSmtpFactory.From, values.GetProperty("smtpFrom").GetString());
        Assert.True(values.GetProperty("smtpPassword").GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task A_change_of_a_fixed_field_is_refused_and_the_recipients_stay_editable()
    {
        var api = site.Api();
        var refused = await api.PutAsync("/api/configure", Json(new { values = new { smtpHost = "evil.example" } }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("smtpHost", await refused.Content.ReadAsStringAsync());

        var ok = await api.PutAsync("/api/configure", Json(new { values = new { smtpEnabled = true, smtpTo = "ops@example.org" } }));
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        using var scope = site.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NotificationSettingsService>().GetAsync();
        Assert.Equal("ops@example.org", stored.SmtpTo);
        Assert.Null(stored.SmtpHost); // nothing of MatSMTP is saved as MatDock's own
    }

    [Fact]
    public async Task The_settings_page_shows_matsmtp_and_saves_nothing_for_the_server()
    {
        var browser = site.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        var html = await (await browser.GetAsync("/Settings/Notifications")).Content.ReadAsStringAsync();
        Assert.Contains("Mail is sent through MatSMTP", html);
        Assert.Contains("<fieldset class=\"mat-fieldset mat-form\" disabled=\"disabled\">", html);
        Assert.DoesNotContain(MatSmtpFactory.Password, html);

        var saved = await browser.PostAsync("/Settings/Notifications?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.SmtpEnabled"] = "true",
            ["Input.SmtpHost"] = "evil.example",
            ["Input.SmtpTo"] = "page@example.org",
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using var scope = site.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NotificationSettingsService>().GetAsync();
        Assert.Equal("page@example.org", stored.SmtpTo);
        Assert.NotEqual("evil.example", stored.SmtpHost);
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}

/// <summary>Without the variables the mail server fields stay MatDock's own and editable.</summary>
public sealed class OwnSmtpSiteTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    [Fact]
    public async Task The_server_fields_are_not_fixed()
    {
        var definition = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api")).RootElement;
        var host = definition.GetProperty("configure").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "smtpHost");
        Assert.False(host.TryGetProperty("fixed", out _));
    }
}
