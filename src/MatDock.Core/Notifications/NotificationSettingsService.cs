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

    public NotificationSettingsService(MatDockDbContext db, ISecretProtector secrets)
    {
        _db = db;
        _secrets = secrets;
    }

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
        // A blank password keeps the stored one (like environment secrets).

        settings.WebhookEnabled = input.WebhookEnabled;
        settings.WebhookUrl = Trim(input.WebhookUrl);

        if (isNew)
        {
            _db.NotificationSettings.Add(settings);
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
