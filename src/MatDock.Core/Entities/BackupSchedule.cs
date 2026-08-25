namespace MatDock.Core.Entities;

/// <summary>
/// A recurring backup job: back up one or more volumes of an environment to a target on a cron schedule,
/// keeping only the configured number/age of archives (retention).
/// </summary>
public class BackupSchedule : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public long EnvironmentId { get; set; }

    /// <summary>Selected volume names, one per line.</summary>
    public string VolumesCsv { get; set; } = string.Empty;

    /// <summary>Target for the archives; <c>null</c> = local storage.</summary>
    public long? BackupTargetId { get; set; }

    /// <summary>Standard 5-field cron expression (UTC).</summary>
    public string Cron { get; set; } = "0 3 * * *";

    /// <summary>Keep only the newest N backups per volume (0 = unlimited).</summary>
    public int RetentionCount { get; set; }

    /// <summary>Delete backups older than N days (0 = disabled).</summary>
    public int RetentionDays { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? LastRunAt { get; set; }

    public string? LastStatus { get; set; }

    public DateTime? NextRunAt { get; set; }

    /// <summary>Parsed, de-duplicated volume names.</summary>
    public IEnumerable<string> Volumes =>
        VolumesCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Distinct(StringComparer.Ordinal);
}
