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

    // --- Alerts (published as events; a schedule "Send a notification" on the event delivers them) ---

    /// <summary>A host whose CPU (load per core) stays above this percentage for two readings is reported (null = no limit).</summary>
    public int? HostCpuLimit { get; set; }

    /// <summary>A host whose memory stays above this percentage for two readings is reported (null = no limit).</summary>
    public int? HostMemLimit { get; set; }

    /// <summary>A host whose disk is fuller than this percentage is reported (null = no limit).</summary>
    public int? HostDiskLimit { get; set; } = 90;

    /// <summary>A container that starts this often within <see cref="RestartLoopMinutes"/> is in a restart loop (0 = not watched).</summary>
    public int RestartLoopCount { get; set; } = 3;

    public int RestartLoopMinutes { get; set; } = 10;
}
