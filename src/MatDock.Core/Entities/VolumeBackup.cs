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

    /// <summary>File name (not full path) of the archive inside the backups directory.</summary>
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string? Note { get; set; }
}
