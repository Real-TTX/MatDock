using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Notifications;

/// <summary>Loads and persists the single notification-settings row; encrypts the SMTP password.</summary>
public sealed class NotificationSettingsService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IMailRelaySource _relay;

    public NotificationSettingsService(MatDockDbContext db, ISecretProtector secrets, IMailRelaySource? relay = null)
    {
        _db = db;
        _secrets = secrets;
        _relay = relay ?? new NoMailRelay();
    }

    /// <summary>The mail relay set from outside (MatSMTP), or null: then the server settings here are fixed.</summary>
    public MailRelay? Relay => _relay.Current;

    /// <summary>Returns the settings row, or a fresh unsaved default if none exists yet.</summary>
    public async Task<NotificationSettings> GetAsync(CancellationToken ct = default)
        => await _db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings();

    public string? DecryptPassword(NotificationSettings settings)
        => _secrets.UnprotectNullable(settings.EncryptedSmtpPassword);

    public async Task UpdateAsync(NotificationSettingsInput input, CancellationToken ct = default)
    {
        var settings = await _db.NotificationSettings.FirstOrDefaultAsync(ct);
        var isNew = settings is null;
        settings ??= new NotificationSettings();
        input = MailServer.KeepFixed(input, settings, Relay);

        settings.NotifyOnSuccess = input.NotifyOnSuccess;
        settings.NotifyOnFailure = input.NotifyOnFailure;

        settings.SmtpEnabled = input.SmtpEnabled;
        settings.SmtpHost = Trim(input.SmtpHost);
        settings.SmtpPort = input.SmtpPort is > 0 and <= 65535 ? input.SmtpPort : 587;
        settings.SmtpUseTls = input.SmtpUseTls;
        settings.SmtpUsername = Trim(input.SmtpUsername);
        settings.SmtpFrom = Trim(input.SmtpFrom);
        settings.SmtpTo = Trim(input.SmtpTo);
        if (!string.IsNullOrEmpty(input.SmtpPassword))
        {
            settings.EncryptedSmtpPassword = _secrets.Protect(input.SmtpPassword);
        }
        else if (input.ClearSmtpPassword)
        {
            settings.EncryptedSmtpPassword = null;
        }
        // A blank password keeps the stored one (like environment secrets).

        settings.WebhookEnabled = input.WebhookEnabled;
        settings.WebhookUrl = Trim(input.WebhookUrl);

        if (isNew)
        {
            _db.NotificationSettings.Add(settings);
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The rules of the notification settings, by the name of the property of <see cref="NotificationSettingsInput"/>: e-mail needs a host, a sender
    /// and recipients, the port is 1 to 65535, the webhook needs an http(s) address. Empty = valid.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Validate(NotificationSettingsInput input, bool relay = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new Dictionary<string, string>();
        if (input.SmtpEnabled && relay)
        {
            // Server, port and sender come from the relay; only the recipients are MatDock's own.
            if (string.IsNullOrWhiteSpace(input.SmtpTo)) { errors[nameof(input.SmtpTo)] = "Email requires recipients."; }
        }
        else if (input.SmtpEnabled)
        {
            if (string.IsNullOrWhiteSpace(input.SmtpHost)) { errors[nameof(input.SmtpHost)] = "Email requires a host."; }
            if (string.IsNullOrWhiteSpace(input.SmtpFrom)) { errors[nameof(input.SmtpFrom)] = "Email requires a sender."; }
            else if (!System.Net.Mail.MailAddress.TryCreate(input.SmtpFrom.Trim(), out _)) { errors[nameof(input.SmtpFrom)] = "Not a valid e-mail address."; }
            if (string.IsNullOrWhiteSpace(input.SmtpTo)) { errors[nameof(input.SmtpTo)] = "Email requires recipients."; }
            if (input.SmtpPort is < 1 or > 65535) { errors[nameof(input.SmtpPort)] = "The port is 1 to 65535."; }
        }

        if (input.WebhookEnabled
            && !(Uri.TryCreate(input.WebhookUrl?.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)))
        {
            errors[nameof(input.WebhookUrl)] = "Please enter a valid http(s) URL.";
        }

        return errors;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
