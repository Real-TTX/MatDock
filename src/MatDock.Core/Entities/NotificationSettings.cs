namespace MatDock.Core.Entities;

/// <summary>
/// Global notification configuration (a single row). Backup/schedule results can be delivered by
/// e-mail (SMTP) and/or an HTTP webhook. The SMTP password is stored encrypted at rest.
/// </summary>
public class NotificationSettings : AuditableEntity
{
    public bool NotifyOnSuccess { get; set; }

    public bool NotifyOnFailure { get; set; } = true;

    // --- SMTP ---
    public bool SmtpEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? EncryptedSmtpPassword { get; set; }
    public string? SmtpFrom { get; set; }

    /// <summary>Recipient address(es), comma- or semicolon-separated.</summary>
    public string? SmtpTo { get; set; }

    // --- Webhook ---
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
}
