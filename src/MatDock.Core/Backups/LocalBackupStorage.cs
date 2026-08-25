using MatDock.Core.Configuration;

namespace MatDock.Core.Backups;

/// <summary>Stores backups in MatDock's own data volume (<c>/data/backups</c>).</summary>
public sealed class LocalBackupStorage : IBackupStorage
{
    private readonly AppPaths _paths;

    public LocalBackupStorage(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<Stream> OpenWriteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.BackupsPath);
        Stream stream = File.Create(FullPath(fileName));
        return Task.FromResult(stream);
    }

    public Task<Stream> OpenReadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = FullPath(fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Die Backup-Datei existiert nicht mehr.", fileName);
        }

        Stream stream = File.OpenRead(path);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = FullPath(fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task TestAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.BackupsPath);
        var probe = Path.Combine(_paths.BackupsPath, $".write-test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Task.CompletedTask;
    }

    private string FullPath(string fileName)
    {
        // Guard against path traversal: only a bare file name is allowed.
        var safe = Path.GetFileName(fileName);
        return Path.Combine(_paths.BackupsPath, safe);
    }
}
