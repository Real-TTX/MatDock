using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Backups;

/// <inheritdoc />
public sealed class BackupStorageFactory : IBackupStorageFactory
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _secrets;
    private readonly MatDockOptions _options;

    public BackupStorageFactory(AppPaths paths, ISecretProtector secrets, IOptions<MatDockOptions> options)
    {
        _paths = paths;
        _secrets = secrets;
        _options = options.Value;
    }

    public IBackupStorage Create(BackupTarget? target)
    {
        if (target is null || target.Type == BackupTargetType.Local)
        {
            return new LocalBackupStorage(_paths);
        }

        if (target.Type == BackupTargetType.Restic)
        {
            var (env, error) = ResticSettings.ParseEnv(_secrets.UnprotectNullable(target.EncryptedResticEnv));
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            var repo = new ResticRepoInfo(
                target.ResticRepository ?? string.Empty,
                _secrets.UnprotectNullable(target.EncryptedResticPassword) ?? string.Empty,
                env);
            return new ResticBackupStorage(repo, _options.ResticPath, Path.Combine(_paths.DataPath, "restic-cache"));
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
