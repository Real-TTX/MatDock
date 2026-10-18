using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using MatDock.Core.Users;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatDock.Core.Status;

/// <summary>What MatDock looks like right now, in numbers (the tiles of the platform contract, MatDesign docs/PLATFORM.md).</summary>
public sealed record InstanceSnapshot
{
    public int Environments { get; init; }

    /// <summary>Enabled environments whose last check failed (offline or error).</summary>
    public int EnvironmentsUnreachable { get; init; }

    /// <summary>Compose projects (managed and discovered) on the reachable environments.</summary>
    public int Stacks { get; init; }

    public int ContainersRunning { get; init; }

    /// <summary>Containers that exist but do not run (exited, created, paused, dead ...).</summary>
    public int ContainersStopped { get; init; }

    /// <summary>Running containers whose Docker health check says unhealthy.</summary>
    public int ContainersUnhealthy { get; init; }

    public int Images { get; init; }

    /// <summary>The disk of the local Docker host (<c>df /</c>), when known.</summary>
    public long? HostDiskUsedBytes { get; init; }

    public long? HostDiskTotalBytes { get; init; }

    /// <summary>The newest finished backup run: a backup schedule's last run or a stack backup.</summary>
    public DateTime? LastBackupUtc { get; init; }

    /// <summary>Whether <see cref="LastBackupUtc"/> succeeded; null without any backup run.</summary>
    public bool? LastBackupOk { get; init; }

    /// <summary>The next run of an enabled timed backup schedule.</summary>
    public DateTime? NextBackupUtc { get; init; }

    /// <summary>Failed backup runs in the last 24 hours: failed stack backups, and backup schedules whose last run failed without one.</summary>
    public int BackupFailures24h { get; init; }

    /// <summary>The size of all stored backup archives.</summary>
    public long BackupBytes { get; init; }
}

/// <summary>A check of <see cref="InstanceStatusService.CheckHealthAsync"/>: <c>ok</c>, <c>degraded</c> or <c>down</c>.</summary>
public sealed record InstanceHealthCheck(string Name, string Status, string? Message = null);

/// <summary>
/// The numbers of the platform tiles (<see cref="InstanceSnapshot"/>), the health checks and the platform actions, from the same services as the
/// dashboard. Docker is only asked on enabled environments that are not known to be unreachable, with a short timeout each, and the answers are
/// cached briefly, so a consumer that polls does not reach out to the hosts each time.
/// </summary>
public sealed class InstanceStatusService
{
    public const string HostCacheKeyPrefix = "platform:host:";
    private static readonly TimeSpan HostTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(8);

    private readonly EnvironmentService _environments;
    private readonly IEnvironmentConnectionService _connection;
    private readonly ContainerService _containers;
    private readonly ScheduleService _schedules;
    private readonly BundleBackupService _bundles;
    private readonly VolumeBackupService _volumeBackups;
    private readonly UserService _users;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InstanceStatusService> _logger;

    public InstanceStatusService(
        EnvironmentService environments,
        IEnvironmentConnectionService connection,
        ContainerService containers,
        ScheduleService schedules,
        BundleBackupService bundles,
        VolumeBackupService volumeBackups,
        UserService users,
        IMemoryCache cache,
        ILogger<InstanceStatusService>? logger = null)
    {
        _environments = environments;
        _connection = connection;
        _containers = containers;
        _schedules = schedules;
        _bundles = bundles;
        _volumeBackups = volumeBackups;
        _users = users;
        _cache = cache;
        _logger = logger ?? NullLogger<InstanceStatusService>.Instance;
    }

    /// <summary>The backup actions of the schedules.</summary>
    public static bool IsBackup(ScheduledTask task) => task.Action is ScheduleAction.Backup or ScheduleAction.BundleBackup;

    public async Task<InstanceSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var all = await _environments.GetAllAsync(ct);
        var enabled = all.Where(e => e.IsEnabled).ToList();
        var hosts = await Task.WhenAll(enabled.Where(IsReachable).Select(e => HostAsync(e, ct)));

        var stacks = new HashSet<(long, string)>();
        foreach (var host in hosts)
        {
            foreach (var c in host.Containers.Where(c => !string.IsNullOrEmpty(c.Project)))
            {
                stacks.Add((host.EnvironmentId, c.Project!));
            }
        }

        var containers = hosts.SelectMany(h => h.Containers).ToList();
        var local = hosts.FirstOrDefault(h => h.IsLocal);
        var backups = await BackupsAsync(ct);

        return backups with
        {
            Environments = all.Count,
            EnvironmentsUnreachable = enabled.Count(e => e.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error),
            Stacks = stacks.Count,
            ContainersRunning = containers.Count(c => c.IsRunning),
            ContainersStopped = containers.Count(c => !c.IsRunning),
            ContainersUnhealthy = containers.Count(IsUnhealthy),
            Images = hosts.Sum(h => h.Images),
            HostDiskUsedBytes = local?.Stats.DiskUsedBytes,
            HostDiskTotalBytes = local?.Stats.DiskTotalBytes,
        };
    }

    /// <summary>The checks of the platform health: the database (down when it cannot be read), the environments and the administrators (degraded).
    /// Cheap: only the database, never a host.</summary>
    public async Task<IReadOnlyList<InstanceHealthCheck>> CheckHealthAsync(CancellationToken ct = default)
    {
        List<DockerEnvironment> all;
        int admins;
        try
        {
            all = await _environments.GetAllAsync(ct);
            admins = await _users.CountActiveAdminsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The database cannot be read");
            return [new InstanceHealthCheck("database", "down", "The database cannot be read.")];
        }

        var checks = new List<InstanceHealthCheck> { new("database", "ok") };
        var unreachable = all.Where(e => e.IsEnabled && e.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error).Select(e => e.Name).ToList();
        checks.Add(unreachable.Count == 0
            ? new InstanceHealthCheck("environments", "ok")
            : new InstanceHealthCheck("environments", "degraded", "Not reachable: " + string.Join(", ", unreachable)));

        // Before the first-run setup there is no user at all; that is not a fault.
        if (admins == 0 && await _users.CountAsync(ct) > 0)
        {
            checks.Add(new InstanceHealthCheck("administrators", "degraded", "No active administrator."));
        }

        return checks;
    }

    /// <summary>Runs every enabled backup schedule (volume and stack backups) now, one after the other; the number of schedules and how many failed.</summary>
    public async Task<(int Ran, int Failed)> RunAllBackupsAsync(CancellationToken ct = default)
    {
        var tasks = (await _schedules.GetAllAsync(ct)).Where(t => t.Enabled && IsBackup(t)).ToList();
        var failed = 0;
        foreach (var task in tasks)
        {
            await _schedules.RunNowAsync(task.Id, ct);
            if ((await _schedules.GetAsync(task.Id, ct))?.LastRunOk == false)
            {
                failed++;
            }
        }

        return (tasks.Count, failed);
    }

    /// <summary>The number of enabled backup schedules (what <see cref="RunAllBackupsAsync"/> would run).</summary>
    public async Task<int> CountBackupSchedulesAsync(CancellationToken ct = default)
        => (await _schedules.GetAllAsync(ct)).Count(t => t.Enabled && IsBackup(t));

    /// <summary>Removes dangling images (<c>docker image prune</c>, not the unused tagged ones) on every reachable environment.</summary>
    public async Task<(bool Ok, int Removed, IReadOnlyList<string> Reclaimed, IReadOnlyList<string> Errors)> PruneDanglingImagesAsync(CancellationToken ct = default)
    {
        var removed = 0;
        var reclaimed = new List<string>();
        var errors = new List<string>();
        foreach (var env in (await _environments.GetEnabledAsync(ct)).Where(IsReachable))
        {
            try
            {
                var result = await _connection.PruneImagesAsync(_environments.BuildSettings(env), all: false, ct);
                if (result.Success)
                {
                    removed += result.Removed;
                    if (!string.IsNullOrEmpty(result.Reclaimed))
                    {
                        reclaimed.Add(result.Reclaimed);
                    }
                }
                else
                {
                    errors.Add($"{env.Name}: {result.Detail}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{env.Name}: {ex.Message}");
            }

            _cache.Remove(HostCacheKeyPrefix + env.Id);
        }

        return (errors.Count == 0, removed, reclaimed, errors);
    }

    /// <summary>"Unhealthy" in the status of <c>docker ps</c> (e.g. <c>Up 3 hours (unhealthy)</c>).</summary>
    public static bool IsUnhealthy(DockerContainer container)
        => container.IsRunning && container.Status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase);

    private static bool IsReachable(DockerEnvironment env)
        => env.IsEnabled && env.Status is not (EnvironmentStatus.Offline or EnvironmentStatus.Error);

    private async Task<InstanceSnapshot> BackupsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddHours(-24);
        var tasks = (await _schedules.GetAllAsync(ct)).Where(IsBackup).ToList();
        var bundles = await _bundles.GetBundlesAsync(ct: ct);
        var archives = await _volumeBackups.GetAllAsync(ct);

        var runs = tasks
            .Where(t => t.LastRunAt is not null)
            .Select(t => (Time: t.LastRunAt!.Value, Ok: t.LastRunOk ?? !t.LastStatus.StartsWithError(), Text: $"{t.Name}: {t.LastStatus}"))
            .Concat(bundles
                .Where(b => b.Status != StackBackupStatus.Running)
                .Select(b => (Time: b.UpdateDate > b.CreateDate ? b.UpdateDate : b.CreateDate, Ok: b.Status == StackBackupStatus.Succeeded,
                    Text: $"{b.StackName}: {b.Message}")))
            .OrderByDescending(r => r.Time)
            .ToList();

        var failedBundles = bundles.Where(b => b.Status == StackBackupStatus.Failed && b.CreateDate >= since).ToList();
        var failedTasks = tasks.Count(t => t.LastRunOk == false && t.LastRunAt >= since
                                           && !failedBundles.Any(b => b.ScheduledTaskId == t.Id));

        var last = runs.Count > 0 ? runs[0] : default;
        return new InstanceSnapshot
        {
            LastBackupUtc = runs.Count > 0 ? last.Time : null,
            LastBackupOk = runs.Count > 0 ? last.Ok : null,
            NextBackupUtc = tasks.Where(t => t.Enabled && t.Trigger == ScheduleTrigger.Cron && t.NextRunAt is not null).Select(t => t.NextRunAt).Min(),
            BackupFailures24h = failedBundles.Count + failedTasks,
            BackupBytes = archives.Sum(a => a.SizeBytes),
        };
    }

    private async Task<HostData> HostAsync(DockerEnvironment env, CancellationToken ct)
    {
        var key = HostCacheKeyPrefix + env.Id;
        if (_cache.TryGetValue(key, out HostData? cached) && cached is not null)
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HostTimeout);
        var settings = _environments.BuildSettings(env);
        var containers = await TryAsync(() => _containers.ListAsync(env, timeout.Token, includeStats: false), (IReadOnlyList<DockerContainer>)[], env);
        var images = await TryAsync(() => _connection.ListImagesAsync(settings, timeout.Token), (IReadOnlyList<DockerImage>)[], env);
        var stats = env.ConnectionType == ConnectionType.Local
            ? await TryAsync(() => _connection.GetHostStatsAsync(settings, timeout.Token), new HostStats(), env)
            : new HostStats();

        var data = new HostData(env.Id, env.ConnectionType == ConnectionType.Local, containers, images.Count, stats);
        _cache.Set(key, data, HostTtl);
        return data;
    }

    private async Task<T> TryAsync<T>(Func<Task<T>> work, T fallback, DockerEnvironment env)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status of environment {Name} not available", env.Name);
            return fallback;
        }
    }


    private sealed record HostData(long EnvironmentId, bool IsLocal, IReadOnlyList<DockerContainer> Containers, int Images, HostStats Stats);
}

internal static class StatusText
{
    /// <summary>A summary of a run that predates <see cref="ScheduledTask.LastRunOk"/>: "Error: ..." or "... Errors: ..." means it failed.</summary>
    public static bool StartsWithError(this string? summary)
        => summary is not null && (summary.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || summary.Contains("Errors:", StringComparison.Ordinal)
                                   || summary.Contains("failed", StringComparison.OrdinalIgnoreCase));
}
