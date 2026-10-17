using MatDock.Core.Entities;

namespace MatDock.Core.Notifications;

/// <summary>Create/update carrier for the notification settings. SmtpPassword is plaintext; blank = keep existing.</summary>
public sealed class NotificationSettingsInput
{
    public bool NotifyOnSuccess { get; set; }
    public bool NotifyOnFailure { get; set; } = true;

    public bool SmtpEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }

    /// <summary>Removes the stored SMTP password (when <see cref="SmtpPassword"/> is blank).</summary>
    public bool ClearSmtpPassword { get; set; }

    public string? SmtpFrom { get; set; }
    public string? SmtpTo { get; set; }

    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }

    /// <summary>The input that saves <paramref name="settings"/> unchanged (the password stays, as it is blank here).</summary>
    public static NotificationSettingsInput From(NotificationSettings settings) => new()
    {
        NotifyOnSuccess = settings.NotifyOnSuccess,
        NotifyOnFailure = settings.NotifyOnFailure,
        SmtpEnabled = settings.SmtpEnabled,
        SmtpHost = settings.SmtpHost,
        SmtpPort = settings.SmtpPort,
        SmtpUseTls = settings.SmtpUseTls,
        SmtpUsername = settings.SmtpUsername,
        SmtpFrom = settings.SmtpFrom,
        SmtpTo = settings.SmtpTo,
        WebhookEnabled = settings.WebhookEnabled,
        WebhookUrl = settings.WebhookUrl,
    };
}
