using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Support;

/// <summary>
/// The readings of a host that the dashboard and the environments show (load, memory, disk; the events of the last day), through
/// <see cref="StaleCache"/>: a page shows the last reading at once and a new one is taken in the background, so one slow host does not hold up the
/// page of all of them. A reading runs in its own scope, as it may outlive the request that asked for it.
/// </summary>
public sealed class HostReadings(IServiceScopeFactory scopes, IMemoryCache cache, HostMetricsHistory history, ILogger<HostReadings> logger)
{
    private static readonly TimeSpan StatsFresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EventsFresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);

    /// <summary>How long a page waits for the first reading of a host before it shows it without.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(3);

    /// <summary>Load, memory and disk of <paramref name="env"/>; empty while the first reading is still on its way or the host does not answer.</summary>
    public async Task<HostStats> StatsAsync(DockerEnvironment env, CancellationToken ct)
        => await StaleCache.GetAsync(cache, "hoststats:" + env.Id, StatsFresh, Keep, FirstWait, async loadCt =>
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var stats = await services.GetRequiredService<IEnvironmentConnectionService>()
                .GetHostStatsAsync(services.GetRequiredService<EnvironmentService>().BuildSettings(env), loadCt);
            history.Add(env.Id, stats);
            return stats;
        }, logger, ct) ?? new HostStats();

    /// <summary>How long a page waits for the first container list of a host (after a change by MatDock it is a first one again).</summary>
    public static readonly TimeSpan ContainersWait = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The containers of <paramref name="env"/> with their usage, for the pages that give an overview (the dashboard, the app tiles): the last list is
    /// shown while a new one is read, so the dashboard does not wait for <c>docker ps</c> of every host on each visit. After something MatDock changed
    /// (<see cref="HostChanges"/>) the list is read anew. Empty while the first one is on its way or the host does not answer.
    /// </summary>
    public async Task<IReadOnlyList<DockerContainer>> ContainersAsync(DockerEnvironment env, CancellationToken ct)
        => await StaleCache.GetAsync<IReadOnlyList<DockerContainer>>(cache, HostChanges.Key("hostcontainers", env.Id), StatsFresh, Keep, ContainersWait, async loadCt =>
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ContainerService>().ListAsync(env, loadCt, includeStats: true);
        }, logger, ct) ?? [];

    /// <summary>The container events of the last day on <paramref name="env"/> (empty while the first reading is on its way).</summary>
    public async Task<IReadOnlyList<ContainerEvent>> EventsAsync(DockerEnvironment env, CancellationToken ct)
        => await StaleCache.GetAsync(cache, "hostevents:" + env.Id, EventsFresh, Keep, FirstWait, async loadCt =>
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ContainerService>().ListEventsAsync(env, 24, loadCt);
        }, logger, ct) ?? [];
}
