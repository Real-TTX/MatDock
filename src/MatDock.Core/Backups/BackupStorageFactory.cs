using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Security;

namespace MatDock.Core.Backups;

/// <inheritdoc />
public sealed class BackupStorageFactory : IBackupStorageFactory
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _secrets;

    public BackupStorageFactory(AppPaths paths, ISecretProtector secrets)
    {
        _paths = paths;
        _secrets = secrets;
    }

    public IBackupStorage Create(BackupTarget? target)
    {
        if (target is null || target.Type == BackupTargetType.Local)
        {
            return new LocalBackupStorage(_paths);
        }

        var info = new SmbConnectionInfo(
            target.SmbHost ?? string.Empty,
            target.SmbShare ?? string.Empty,
            target.SmbDirectory,
            target.SmbUsername,
            _secrets.UnprotectNullable(target.EncryptedSmbPassword),
            target.SmbDomain);

        return new SmbBackupStorage(info);
    }
}
