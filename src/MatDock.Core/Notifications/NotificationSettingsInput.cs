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
    public string? SmtpFrom { get; set; }
    public string? SmtpTo { get; set; }

    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
}
