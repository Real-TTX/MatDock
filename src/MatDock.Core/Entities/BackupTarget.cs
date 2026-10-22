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

    // --- restic ---

    /// <summary>The restic repository: <c>s3:host/bucket/path</c>, <c>b2:bucket:path</c>, <c>rest:https://…</c>, <c>azure:</c>, <c>gs:</c>,
    /// <c>swift:</c> or an absolute path inside the MatDock container.</summary>
    public string? ResticRepository { get; set; }

    /// <summary>The repository password (restic encrypts everything with it), protected at rest.</summary>
    public string? EncryptedResticPassword { get; set; }

    /// <summary>The access keys of the repository's backend as <c>NAME=value</c> lines (<c>AWS_ACCESS_KEY_ID</c>, <c>B2_ACCOUNT_KEY</c>, …), protected at rest.</summary>
    public string? EncryptedResticEnv { get; set; }
}
