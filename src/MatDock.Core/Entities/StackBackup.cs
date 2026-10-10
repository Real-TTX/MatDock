namespace MatDock.Core.Entities;

/// <summary>
/// A full backup of one stack ("bundle"): a manifest file (<c>.mdbundle.json</c>: compose, <c>.env</c>, image
/// references) plus one <see cref="VolumeBackup"/> archive per volume (linked by <see cref="VolumeBackup.StackBackupId"/>),
/// all at the same backup target. The row exists from the start of the backup (status Running), so failed attempts
/// stay visible. Mapped to the <c>StackBackup</c> table.
/// </summary>
public class StackBackup : AuditableEntity
{
    /// <summary>Environment the stack ran on (may no longer exist).</summary>
    public long EnvironmentId { get; set; }

    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>Compose project name of the stack.</summary>
    public string StackName { get; set; } = string.Empty;

    public StackBackupKind Kind { get; set; } = StackBackupKind.Manual;

    public StackBackupStatus Status { get; set; } = StackBackupStatus.Running;

    /// <summary>File name of the manifest at the target; null until it is written.</summary>
    public string? ManifestFileName { get; set; }

    /// <summary>Which target holds the bundle; <c>null</c> = local (/data/backups).</summary>
    public long? BackupTargetId { get; set; }

    public string? BackupTargetName { get; set; }

    /// <summary>Total size of the volume archives and the manifest.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Number of volumes backed up successfully.</summary>
    public int VolumeCount { get; set; }

    /// <summary>True when the containers were stopped during the backup (a consistent snapshot).</summary>
    public bool Consistent { get; set; }

    public string? Note { get; set; }

    /// <summary>Summary of the run (or the error of a failed one).</summary>
    public string? Message { get; set; }

    /// <summary>The ScheduledTask (BundleBackup action) that created this backup; retention only prunes its own bundles.</summary>
    public long? ScheduledTaskId { get; set; }
}
