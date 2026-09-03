using MatDock.Core.Entities;

namespace MatDock.Core.Backups;

/// <summary>A backup archive file present at a storage target.</summary>
public sealed record BackupFileInfo(string FileName, long SizeBytes, DateTime? ModifiedUtc);

/// <summary>Abstracts where backup archives are written/read (local data volume or a network share).</summary>
public interface IBackupStorage
{
    Task<Stream> OpenWriteAsync(string fileName, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string fileName, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>Lists the archive files present at this storage (best-effort; empty if not listable).</summary>
    Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Verifies the target is reachable/writable. Throws with a descriptive message on failure.</summary>
    Task TestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates the right <see cref="IBackupStorage"/> for a target (null = local).</summary>
public interface IBackupStorageFactory
{
    IBackupStorage Create(BackupTarget? target);
}
