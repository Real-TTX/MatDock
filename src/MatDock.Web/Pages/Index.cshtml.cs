using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using MatDock.Core.Status;
using MatDock.Core.Volumes;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages;

public class IndexModel : PageModel
{
    // Short TTLs so the landing page stays fresh but repeated visits / refreshes don't re-open SSH each time.
    private static readonly TimeSpan StatsTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EventsTtl = TimeSpan.FromSeconds(60);

    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly ContainerService _containerService;
    private readonly ScheduleService _scheduleService;
    private readonly BundleBackupService _bundleService;
    private readonly VolumeBackupService _volumeBackupService;
    private readonly AppLaunchpadService _launchpad;
    private readonly IMemoryCache _cache;
    private readonly HostMetricsHistory _history;
    private readonly MatDock.Core.Updates.UpdateCheckCache _updates;

    public IndexModel(
        EnvironmentService environmentService,
        IEnvironmentConnectionService connectionService,
        ContainerService containerService,
        ScheduleService scheduleService,
        BundleBackupService bundleService,
        VolumeBackupService volumeBackupService,
        AppLaunchpadService launchpad,
        IMemoryCache cache,
        HostMetricsHistory history,
        MatDock.Core.Updates.UpdateCheckCache updates)
    {
        _history = history;
        _updates = updates;
        _environmentService = environmentService;
        _connectionService = connectionService;
        _containerService = containerService;
        _scheduleService = scheduleService;
        _bundleService = bundleService;
        _volumeBackupService = volumeBackupService;
        _launchpad = launchpad;
        _cache = cache;
    }

    public long? SelectedEnvId { get; private set; }
    public bool IsAll => SelectedEnvId is null;

    // KPI tiles
    public int TotalEnvironments { get; private set; }
    public int OnlineCount { get; private set; }
    public int AttentionCount { get; private set; }
    public int DisabledCount { get; private set; }
    public int RunningContainersTotal { get; private set; }
    public int StacksCount { get; private set; }

    /// <summary>Stacks that had something to update at the last look, and how many stacks were looked at (a look is made by the Updates page, an update rule or a schedule).</summary>
    public int UpdatesAvailable { get; private set; }
    public int UpdatesChecked { get; private set; }

    // Per-environment usage tiles
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public Dictionary<long, HostStats> Stats { get; } = new();
    public Dictionary<long, int> RunningCounts { get; } = new();

    public List<TopContainer> TopContainers { get; private set; } = new();
    public List<EventRow> RecentEvents { get; private set; } = new();

    // App launchpad tiles (managed stacks with app metadata) for the "Apps" section.
    public List<AppTile> AppTiles { get; private set; } = new();

    // Backup status
    public int BackupCount { get; private set; }
    public long BackupBytes { get; private set; }
    public DateTime? LastBackupUtc { get; private set; }
    public DateTime? NextBackupUtc { get; private set; }
    public int SchedulesEnabledCount { get; private set; }
    public DateTime? LastRunUtc { get; private set; }
    public string? LastRunSummary { get; private set; }
    public bool? LastRunOk { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var all = await _environmentService.GetAllAsync(ct);

        // Honor the global environment selector; drop a stale selection (deleted/disabled env → "all").
        var selected = EnvSelection.Resolve(HttpContext);
        if (selected is { } sid && !all.Any(e => e.Id == sid && e.IsEnabled))
        {
            EnvSelection.Clear(HttpContext);
            selected = null;
        }

        SelectedEnvId = selected;

        // KPI counts reflect the current scope: one env when selected, otherwise the whole fleet.
        var kpiSet = selected is { } id ? all.Where(e => e.Id == id).ToList() : all;
        TotalEnvironments = kpiSet.Count;
        var kpiActive = kpiSet.Where(e => e.IsEnabled).ToList();
        OnlineCount = kpiActive.Count(e => e.Status == EnvironmentStatus.Online);
        AttentionCount = kpiActive.Count(e => e.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error);
        DisabledCount = kpiSet.Count(e => !e.IsEnabled);

        // Only ever reach out to enabled hosts in scope.
        Environments = (selected is { } sel ? all.Where(e => e.Id == sel) : all.Where(e => e.IsEnabled))
            .OrderBy(e => e.Name)
            .ToList();

        await LoadHostDataAsync(Environments, ct);
        await LoadBackupStatusAsync(selected, ct);

        // Apps (managed stacks with app metadata) — reuses the container cache warmed above.
        AppTiles = await _launchpad.GetTilesAsync(selected, ct);
    }

    private async Task LoadHostDataAsync(IReadOnlyList<DockerEnvironment> environments, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);

        var tasks = environments.Select(async env =>
        {
            // Serve from cache where possible; only take a gate slot when we actually hit the host.
            _cache.TryGetValue($"hoststats:{env.Id}", out HostStats? stats);
            _cache.TryGetValue($"dash:containers:{env.Id}", out IReadOnlyList<DockerContainer>? containers);
            _cache.TryGetValue($"dash:events:{env.Id}", out IReadOnlyList<ContainerEvent>? events);

            if (stats is null || containers is null || events is null)
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (stats is null)
                    {
                        try { stats = await _connectionService.GetHostStatsAsync(_environmentService.BuildSettings(env), ct); }
                        catch { stats = new HostStats(); }
                        _cache.Set($"hoststats:{env.Id}", stats, StatsTtl);
                        _history.Add(env.Id, stats);
                    }

                    if (containers is null)
                    {
                        try { containers = await _containerService.ListAsync(env, ct, includeStats: true); }
                        catch { containers = Array.Empty<DockerContainer>(); }
                        _cache.Set($"dash:containers:{env.Id}", containers, StatsTtl);
                    }

                    if (events is null)
                    {
                        events = await _containerService.ListEventsAsync(env, 24, ct); // best-effort inside
                        _cache.Set($"dash:events:{env.Id}", events, EventsTtl);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            return (
                env,
                stats: stats ?? new HostStats(),
                containers: containers ?? Array.Empty<DockerContainer>(),
                events: events ?? Array.Empty<ContainerEvent>());
        });

        var results = await Task.WhenAll(tasks);

        var topAll = new List<TopContainer>();
        var eventsAll = new List<EventRow>();
        var stackKeys = new HashSet<(long, string)>();

        foreach (var r in results)
        {
            Stats[r.env.Id] = r.stats;
            var running = r.containers.Count(c => c.IsRunning);
            RunningCounts[r.env.Id] = running;
            RunningContainersTotal += running;

            // Count distinct compose projects (managed + discovered) as "stacks".
            foreach (var c in r.containers.Where(c => !string.IsNullOrEmpty(c.Project)))
            {
                stackKeys.Add((r.env.Id, c.Project!));
            }

            foreach (var c in r.containers.Where(c => c.IsRunning))
            {
                topAll.Add(new TopContainer(r.env.Name, r.env.Id, c));
            }

            foreach (var e in r.events.Where(e => e.IsLifecycle))
            {
                eventsAll.Add(new EventRow(r.env.Name, e));
            }
        }

        StacksCount = stackKeys.Count;
        foreach (var (envId, project) in stackKeys)
        {
            if (_updates.Plan(envId, project) is { } looked)
            {
                UpdatesChecked++;
                UpdatesAvailable += looked.Plan.Items.Count > 0 ? 1 : 0;
            }
        }

        TopContainers = topAll
            .OrderByDescending(t => t.Container.CpuPercent ?? -1)
            .ThenByDescending(t => t.Container.MemPercent ?? -1)
            .Take(6)
            .ToList();

        RecentEvents = eventsAll
            .OrderByDescending(e => e.Event.TimeUtc)
            .Take(12)
            .ToList();
    }

    private async Task LoadBackupStatusAsync(long? selected, CancellationToken ct)
    {
        // The backup schedules are tasks of the Schedules page (volume backups and stack bundles).
        var schedules = (await _scheduleService.GetAllAsync(ct)).Where(InstanceStatusService.IsBackup).ToList();
        var backups = await _volumeBackupService.GetAllAsync(ct);
        var bundles = await _bundleService.GetBundlesAsync(selected, ct: ct);

        if (selected is { } envId)
        {
            schedules = schedules.Where(s => s.EnvironmentId is null || s.EnvironmentId == envId).ToList();
            backups = backups.Where(b => b.SourceEnvironmentId == envId).ToList();
        }

        // A user limited to some environments gets the figures of those only (a schedule for "all environments" is not theirs to see).
        if (await _environmentService.AllowedEnvironmentIdsAsync(ct) is { } allowed)
        {
            schedules = schedules.Where(s => s.EnvironmentId is { } e && allowed.Contains(e)).ToList();
            backups = backups.Where(b => b.SourceEnvironmentId is { } e && allowed.Contains(e)).ToList();
            bundles = bundles.Where(b => allowed.Contains(b.EnvironmentId)).ToList();
        }

        // A bundle stores its volumes as volume backups too, so the archives are the count and size.
        BackupCount = backups.Count;
        BackupBytes = backups.Sum(b => b.SizeBytes);
        var newest = backups.Select(b => b.CreateDate).Concat(bundles.Select(b => b.CreateDate)).ToList();
        LastBackupUtc = newest.Count > 0 ? newest.Max() : null;

        var enabled = schedules.Where(s => s.Enabled && s.Trigger == ScheduleTrigger.Cron).ToList();
        SchedulesEnabledCount = enabled.Count;
        var nexts = enabled.Where(s => s.NextRunAt.HasValue).Select(s => s.NextRunAt!.Value).ToList();
        NextBackupUtc = nexts.Count > 0 ? nexts.Min() : null;

        var lastRun = schedules
            .Where(s => s.LastRunAt.HasValue)
            .OrderByDescending(s => s.LastRunAt)
            .FirstOrDefault();
        if (lastRun is not null)
        {
            LastRunUtc = lastRun.LastRunAt;
            LastRunSummary = lastRun.LastStatus;
            LastRunOk = lastRun.LastRunOk ?? (string.IsNullOrEmpty(lastRun.LastStatus) ? null : !lastRun.LastStatus.StartsWithError());
        }
    }

    public sealed record TopContainer(string EnvName, long EnvId, DockerContainer Container);
    public sealed record EventRow(string EnvName, ContainerEvent Event);
}
