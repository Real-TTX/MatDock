using System.Net.Http;
using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Notifications;

public interface INotificationService
{
    /// <summary>Sends a backup result notification via the configured channels (respecting the on-success/on-failure prefs).
    /// <paramref name="title"/> labels the source — a schedule name, "Manual backup: …" or "Bulk backup".</summary>
    Task NotifyBackupResultAsync(string title, string summary, bool success, CancellationToken ct = default);

    /// <summary>Sends a test notification via all enabled channels; returns an aggregated result.</summary>
    Task<(bool Ok, string Message)> SendTestAsync(CancellationToken ct = default);

    /// <summary>Sends a generic notification (e.g. a scheduled report or a schedule run result) via all
    /// enabled channels, regardless of the backup success/failure preferences. Best-effort per channel.</summary>
    Task<(bool Ok, string Message)> NotifyAsync(string subject, string body, CancellationToken ct = default);
}

/// <summary>Delivers notifications by e-mail (SMTP) and/or HTTP webhook. Every channel is best-effort.</summary>
public sealed class NotificationService : INotificationService
{
    private readonly NotificationSettingsService _settingsService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(NotificationSettingsService settingsService, IHttpClientFactory httpFactory, ILogger<NotificationService> logger)
    {
        _settingsService = settingsService;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task NotifyBackupResultAsync(string title, string summary, bool success, CancellationToken ct = default)
    {
        var s = await _settingsService.GetAsync(ct);
        if (success ? !s.NotifyOnSuccess : !s.NotifyOnFailure)
        {
            return;
        }

        // Strip CR/LF: a line break would make MailMessage.Subject throw (and silently drop the e-mail).
        var safeName = title.Replace('\r', ' ').Replace('\n', ' ');
        var subject = $"MatDock Backup: {safeName} – {(success ? "OK" : "Error")}";
        await DispatchAsync(s, subject, summary, safeName, success, ct);
    }

    public async Task<(bool Ok, string Message)> SendTestAsync(CancellationToken ct = default)
    {
        var s = await _settingsService.GetAsync(ct);
        if (!s.SmtpEnabled && !s.WebhookEnabled)
        {
            return (false, "No notification enabled (SMTP and webhook are off).");
        }

        var errors = await DispatchAsync(s, "MatDock test notification",
            "This is a test notification from MatDock.", "Test", success: true, ct);
        return errors.Count == 0
            ? (true, "Test notification sent.")
            : (false, string.Join(" · ", errors));
    }

    public async Task<(bool Ok, string Message)> NotifyAsync(string subject, string body, CancellationToken ct = default)
    {
        var s = await _settingsService.GetAsync(ct);
        if (!s.SmtpEnabled && !s.WebhookEnabled)
        {
            return (false, "No notification channel enabled (SMTP and webhook are off).");
        }

        var safeSubject = subject.Replace('\r', ' ').Replace('\n', ' ');
        var errors = await DispatchAsync(s, safeSubject, body, safeSubject, success: true, ct);
        return errors.Count == 0 ? (true, "Notification sent.") : (false, string.Join(" · ", errors));
    }

    private async Task<List<string>> DispatchAsync(NotificationSettings s, string subject, string body, string scheduleName, bool success, CancellationToken ct)
    {
        var errors = new List<string>();

        if (s.SmtpEnabled)
        {
            try { await SendEmailAsync(s, subject, body, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "E-mail notification failed."); errors.Add($"Email: {ex.Message}"); }
        }

        if (s.WebhookEnabled)
        {
            try { await SendWebhookAsync(s, subject, body, scheduleName, success, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Webhook notification failed."); errors.Add($"Webhook: {ex.Message}"); }
        }

        return errors;
    }

    private async Task SendEmailAsync(NotificationSettings s, string subject, string body, CancellationToken ct)
    {
        var mail = MailServer.Compose(s, _settingsService.DecryptPassword(s), _settingsService.Relay);
        if (string.IsNullOrWhiteSpace(mail.Host) || string.IsNullOrWhiteSpace(mail.From) || string.IsNullOrWhiteSpace(s.SmtpTo))
        {
            throw new InvalidOperationException("SMTP is incompletely configured (host, sender and recipients are required).");
        }

        var message = new MimeKit.MimeMessage();
        message.From.Add(MimeKit.MailboxAddress.Parse(mail.From.Trim()));
        foreach (var to in s.SmtpTo.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MimeKit.MailboxAddress.TryParse(to, out var address))
            {
                message.To.Add(address);
            }
        }

        if (message.To.Count == 0)
        {
            throw new InvalidOperationException("No valid recipient.");
        }

        message.Subject = subject;
        message.Body = new MimeKit.TextPart("plain") { Text = body };

        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(mail.Host.Trim(), mail.Port, mail.SocketOptions, ct);
        if (mail.Username is not null)
        {
            await client.AuthenticateAsync(mail.Username, mail.Password ?? string.Empty, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private async Task SendWebhookAsync(NotificationSettings s, string subject, string body, string scheduleName, bool success, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.WebhookUrl)
            || !Uri.TryCreate(s.WebhookUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Invalid webhook URL (http/https expected).");
        }

        var payload = JsonSerializer.Serialize(new
        {
            source = "MatDock",
            title = scheduleName,
            schedule = scheduleName, // kept for backward compatibility with existing webhook consumers
            success,
            subject,
            message = body,
            timestamp = DateTime.UtcNow
        });

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(uri, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }
    }
}
