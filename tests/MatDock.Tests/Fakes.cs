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

public sealed class FakeConnectionService : IEnvironmentConnectionService
{
    public SshConnectionSettings? LastSettings { get; private set; }

    /// <summary>What <see cref="GetHostStatsAsync"/> answers.</summary>
    public HostStats Stats { get; set; } = new();

    /// <summary>What <see cref="ListImagesAsync"/> answers.</summary>
    public IReadOnlyList<DockerImage> Images { get; set; } = new List<DockerImage>();

    /// <summary>What <see cref="PruneImagesAsync"/> answers.</summary>
    public PruneResult ImagePrune { get; set; } = new(true, 0, string.Empty);

    /// <summary>How often <see cref="PruneImagesAsync"/> was called, and with <c>all</c> last.</summary>
    public int ImagePrunes { get; private set; }

    public bool? LastPruneAll { get; private set; }

    /// <summary>The SSH host key the fake server shows in a connection test.</summary>
    public string? HostKey { get; set; }

    public Task<DockerConnectionResult> TestConnectionAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        var ok = DockerConnectionResult.Ok("ok", "27.0", "1.47", "linux/amd64");
        return Task.FromResult(HostKey is null ? ok : new DockerConnectionResult
        {
            Success = true, Status = ok.Status, Message = ok.Message, DockerVersion = ok.DockerVersion, HostKeyFingerprint = HostKey,
        });
    }

    public Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<IReadOnlyList<DockerVolume>>(new List<DockerVolume>());
    }

    public Task<(IReadOnlyList<DockerVolume> Volumes, IReadOnlyCollection<string> UnusedNames)> ListVolumesWithUsageAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<(IReadOnlyList<DockerVolume>, IReadOnlyCollection<string>)>((new List<DockerVolume>(), new HashSet<string>()));
    }

    public Task<HostStats> GetHostStatsAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(Stats);
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

    public Task<IReadOnlyList<DockerUnusedVolume>> ListUnusedVolumesDetailedAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<IReadOnlyList<DockerUnusedVolume>>(new List<DockerUnusedVolume>());
    }

    public Task<PruneResult> RemoveVolumesAsync(SshConnectionSettings settings, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(new PruneResult(true, names.Count, string.Empty));
    }

    public Task<(IReadOnlyList<DockerNetwork> Networks, IReadOnlyCollection<string> UnusedNames)> ListNetworksWithUsageAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<(IReadOnlyList<DockerNetwork>, IReadOnlyCollection<string>)>((new List<DockerNetwork>(), new HashSet<string>()));
    }

    public Task<DockerNetworkDetail?> InspectNetworkAsync(SshConnectionSettings settings, string nameOrId, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult<DockerNetworkDetail?>(new DockerNetworkDetail { Name = nameOrId, Driver = "bridge" });
    }

    public Task<(bool Ok, string Message)> RemoveNetworkAsync(SshConnectionSettings settings, string nameOrId, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }

    public Task<PruneResult> PruneNetworksAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(new PruneResult(true, 0, string.Empty));
    }

    public Task<IReadOnlyList<DockerImage>> ListImagesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(Images);
    }

    public Task<(bool Ok, string Message)> RemoveImageAsync(SshConnectionSettings settings, string reference, bool force, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }

    public Task<(bool Ok, string Message)> PullImageAsync(SshConnectionSettings settings, string reference, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }

    public Task<(bool Ok, string Message)> CreateNetworkAsync(SshConnectionSettings settings, string name, string? driver, bool @internal, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }

    public Task<(bool Ok, string Message)> ConnectContainerToNetworkAsync(SshConnectionSettings settings, string network, string container, bool connect, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult((true, "ok"));
    }

    public Task<PruneResult> PruneImagesAsync(SshConnectionSettings settings, bool all, CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        ImagePrunes++;
        LastPruneAll = all;
        return Task.FromResult(ImagePrune);
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

/// <summary>Host sessions for services whose tested paths never reach a host: any use fails the test.</summary>
internal sealed class NoHostSessionFactory : MatDock.Core.Execution.IHostSessionFactory
{
    public MatDock.Core.Execution.IHostSession Create(SshConnectionSettings settings)
        => throw new InvalidOperationException("This test must not run host commands.");
}

/// <summary>Swallows schedule events.</summary>
internal sealed class NoopScheduleEventBus : MatDock.Core.Schedules.IScheduleEventBus
{
    public Task PublishAsync(ScheduleEvent evt, long? environmentId = null, string? detail = null, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Builds the real services over a <see cref="TestDatabase"/> (no host access).</summary>
internal static class TestServices
{
    public static MatDock.Core.Stacks.StackService Stacks(TestDatabase db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MatDock.Core.Configuration.MatDockOptions());
        var secrets = new FakeSecretProtector();
        return new MatDock.Core.Stacks.StackService(
            db.Context,
            new NoHostSessionFactory(),
            new EnvironmentService(db.Context, secrets, new FakeConnectionService(), options),
            new MatDock.Core.Git.GitCredentialService(db.Context, secrets),
            new MatDock.Core.Git.GitRepositoryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<MatDock.Core.Git.GitRepositoryService>.Instance),
            new MatDock.Core.Registries.RegistryService(db.Context, secrets,
                new MatDock.Core.Registries.RegistryApiClient(new NoHttpClientFactory(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<MatDock.Core.Registries.RegistryApiClient>.Instance)),
            new NoopScheduleEventBus(),
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatDock.Core.Stacks.StackService>.Instance);
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP in this test.");
    }
}
