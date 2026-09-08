namespace MatDock.Core.Entities;

/// <summary>
/// A completed backup of a Docker volume, stored as a tar archive in the MatDock data volume.
/// Environment name and volume name are snapshotted so the backup remains meaningful even if the
/// source environment is later deleted.
/// </summary>
public class VolumeBackup : AuditableEntity
{
    /// <summary>Source environment id at backup time (may no longer exist).</summary>
    public long? SourceEnvironmentId { get; set; }

    public string SourceEnvironmentName { get; set; } = string.Empty;

    public string VolumeName { get; set; } = string.Empty;

    /// <summary>File name (not full path) of the archive inside the target's backups location.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Which target holds this archive; <c>null</c> = local (/data/backups).</summary>
    public long? BackupTargetId { get; set; }

    public string? BackupTargetName { get; set; }

    /// <summary>The schedule that created this backup; <c>null</c> for manual/on-demand backups. Retention only prunes a schedule's own archives.</summary>
    public long? BackupScheduleId { get; set; }

    /// <summary>The ScheduledTask (Backup action) that created this backup; <c>null</c> otherwise. Retention for a
    /// Schedules-driven backup only prunes that task's own archives.</summary>
    public long? ScheduledTaskId { get; set; }

    public long SizeBytes { get; set; }

    public string? Note { get; set; }
}
