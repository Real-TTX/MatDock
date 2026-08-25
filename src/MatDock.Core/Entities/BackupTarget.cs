namespace MatDock.Core.Entities;

/// <summary>
/// A destination where backups are written. <see cref="BackupTargetType.Local"/> uses MatDock's data
/// volume; <see cref="BackupTargetType.Smb"/> writes to a network share (NAS). SMB credentials are
/// stored encrypted at rest.
/// </summary>
public class BackupTarget : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public BackupTargetType Type { get; set; } = BackupTargetType.Local;

    /// <summary>Preselected target for new backups/schedules.</summary>
    public bool IsDefault { get; set; }

    // --- SMB / CIFS ---
    public string? SmbHost { get; set; }

    public string? SmbShare { get; set; }

    /// <summary>Optional subdirectory within the share (e.g. "matdock/backups").</summary>
    public string? SmbDirectory { get; set; }

    public string? SmbUsername { get; set; }

    public string? SmbDomain { get; set; }

    public string? EncryptedSmbPassword { get; set; }
}
