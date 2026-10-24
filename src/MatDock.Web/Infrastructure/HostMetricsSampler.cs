using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Schedules;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Every five minutes reads CPU, memory and disk of every enabled, reachable environment and the stats of its containers: into
/// <see cref="HostMetricsHistory"/> (the cards), the database (<see cref="MetricsStore"/>, the charts, folded and trimmed once an hour) and
/// <see cref="MetricsSnapshot"/> (<c>/metrics</c>). Publishes <see cref="ScheduleEvent.HostThreshold"/> when a host goes over one of its limits or comes
/// back (<see cref="HostLimitWatch"/>).
/// </summary>
public sealed class HostMetricsSampler(IServiceScopeFactory scopes, HostMetricsHistory history, MetricsSnapshot snapshot, ILogger<HostMetricsSampler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CompactEvery = TimeSpan.FromHours(1);

    private readonly HostLimitWatch _limits = new();
    private DateTime _compacted = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Host metrics sampler failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>The cards show the last 24 hours at once after a restart: the history is filled from the database.</summary>
    private async Task SeedAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<MetricsStore>();
            foreach (var env in await scope.ServiceProvider.GetRequiredService<EnvironmentService>().GetEnabledAsync(ct))
            {
                var points = await store.GetAsync(env.Id, null, HostMetricsHistory.Keep, DateTime.UtcNow, ct);
                history.Seed(env.Id, points.Select(p => new HostSample(p.TimeUtc, p.Cpu, p.Mem, p.Disk ?? 0)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Host metrics history not seeded.");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var environments = scope.ServiceProvider.GetRequiredService<EnvironmentService>();
        var connection = scope.ServiceProvider.GetRequiredService<IEnvironmentConnectionService>();
        var containers = scope.ServiceProvider.GetRequiredService<ContainerService>();
        var store = scope.ServiceProvider.GetRequiredService<MetricsStore>();
        var bus = scope.ServiceProvider.GetRequiredService<IScheduleEventBus>();
        var settings = await scope.ServiceProvider.GetRequiredService<NotificationSettingsService>().GetAsync(ct);
        var enabled = await environments.GetEnabledAsync(ct);
        foreach (var env in enabled)
        {
            if (env.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error)
            {
                snapshot.Remove(env.Id);
                continue;
            }

            try
            {
                var now = DateTime.UtcNow;
                var stats = await connection.GetHostStatsAsync(environments.BuildSettings(env), ct);
                history.Add(env.Id, stats);
                IReadOnlyList<DockerContainer> running = [];
                try
                {
                    running = await containers.ListAsync(env, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Container stats of {Environment} not read.", env.Name);
                }

                await store.RecordAsync(env.Id, stats, running, now, ct);
                snapshot.Set(new MetricsSnapshot.Reading(env, now, stats, running));
                foreach (var message in _limits.Check(env, stats, HostLimits.Of(settings, env)))
                {
                    await bus.PublishAsync(ScheduleEvent.HostThreshold, env.Id, message, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Host metrics of {Environment} not read.", env.Name);
            }
        }

        foreach (var gone in snapshot.All.Where(r => enabled.All(e => e.Id != r.Environment.Id)).ToList())
        {
            snapshot.Remove(gone.Environment.Id);
        }

        if (DateTime.UtcNow - _compacted >= CompactEvery)
        {
            _compacted = DateTime.UtcNow;
            await store.CompactAsync(_compacted, ct);
        }
    }
}
