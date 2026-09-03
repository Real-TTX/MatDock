using MatDock.Core.Backups;
using MatDock.Core.Data;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Security;
using MatDock.Core.Ssh;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Tests;

internal sealed class FakeCurrentUser : ICurrentUserAccessor
{
    public FakeCurrentUser(long? userId) => UserId = userId;
    public long? UserId { get; }
}

/// <summary>Reversible, non-cryptographic protector for tests.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private const string Prefix = "enc:";
    public string Protect(string plaintext) => Prefix + plaintext;
    public string Unprotect(string protectedValue) => protectedValue.StartsWith(Prefix) ? protectedValue[Prefix.Length..] : protectedValue;
    public string? ProtectNullable(string? plaintext) => string.IsNullOrEmpty(plaintext) ? null : Protect(plaintext);
    public string? UnprotectNullable(string? protectedValue) => string.IsNullOrEmpty(protectedValue) ? null : Unprotect(protectedValue);
}

internal sealed class FakeConnectionService : IEnvironmentConnectionService
{
    public SshConnectionSettings? LastSettings { get; private set; }

    public Task<DockerConnectionResult> TestConnectionAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(DockerConnectionResult.Ok("ok", "27.0", "1.47", "linux/amd64"));
    }

    public Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<IReadOnlyList<DockerVolume>>(new List<DockerVolume>());
    }

    public Task<HostStats> GetHostStatsAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(new HostStats());
    }

    public Task<DockerVolumeDetail?> InspectVolumeAsync(SshConnectionSettings settings, string volume, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<DockerVolumeDetail?>(new DockerVolumeDetail { Name = volume, Driver = "local" });
    }

    public Task<(bool Ok, string Message)> CreateVolumeAsync(SshConnectionSettings settings, string name, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }

    public Task<(bool Ok, string Message)> CreateVolumeAsync(SshConnectionSettings settings, string name, string? driver, IReadOnlyList<(string Key, string Value)> options, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }
}

/// <summary>No-op storage factory for service tests that don't touch real storage.</summary>
internal sealed class FakeBackupStorageFactory : IBackupStorageFactory
{
    public IBackupStorage Create(BackupTarget? target) => new NoopStorage();

    private sealed class NoopStorage : IBackupStorage
    {
        public Task<Stream> OpenWriteAsync(string fileName, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<Stream> OpenReadAsync(string fileName, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string fileName, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackupFileInfo>>(System.Array.Empty<BackupFileInfo>());
        public Task TestAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>Creates a MatDockDbContext backed by an isolated in-memory SQLite database.</summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDatabase(long? currentUserId = 1)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MatDockDbContext>()
            .UseSqlite(_connection)
            .Options;
        Context = new MatDockDbContext(options, new FakeCurrentUser(currentUserId));
        Context.Database.EnsureCreated();
    }

    public MatDockDbContext Context { get; }

    /// <summary>A fresh context over the same connection (to read without the first context's tracking).</summary>
    public MatDockDbContext NewContext(long? currentUserId = 1)
    {
        var options = new DbContextOptionsBuilder<MatDockDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new MatDockDbContext(options, new FakeCurrentUser(currentUserId));
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
