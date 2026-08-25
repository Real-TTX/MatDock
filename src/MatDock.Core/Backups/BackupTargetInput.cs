using MatDock.Core.Entities;

namespace MatDock.Core.Backups;

/// <summary>Create/update carrier for a backup target. SMB password is plaintext here (kept if blank on edit).</summary>
public sealed class BackupTargetInput
{
    public string Name { get; set; } = string.Empty;
    public BackupTargetType Type { get; set; } = BackupTargetType.Smb;
    public bool IsDefault { get; set; }

    public string? SmbHost { get; set; }
    public string? SmbShare { get; set; }
    public string? SmbDirectory { get; set; }
    public string? SmbUsername { get; set; }
    public string? SmbDomain { get; set; }
    public string? SmbPassword { get; set; }
}
