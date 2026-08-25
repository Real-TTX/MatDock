namespace MatDock.Core.Backups;

/// <summary>Create/update carrier for a backup schedule.</summary>
public sealed class BackupScheduleInput
{
    public string Name { get; set; } = string.Empty;
    public long EnvironmentId { get; set; }

    /// <summary>Selected volume names, one per line.</summary>
    public string VolumesCsv { get; set; } = string.Empty;

    public long? BackupTargetId { get; set; }
    public string Cron { get; set; } = "0 3 * * *";
    public int RetentionCount { get; set; }
    public int RetentionDays { get; set; }
    public bool Enabled { get; set; } = true;
}
