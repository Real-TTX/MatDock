using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Periodically probes enabled environments and publishes <see cref="ScheduleEvent.EnvironmentOffline"/>
/// when one transitions from reachable to unreachable, so event-triggered schedules can react. State is
/// held in memory; the first observation only establishes a baseline (no event).
/// </summary>
public sealed class EnvironmentHealthMonitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnvironmentHealthMonitor> _logger;
    private readonly Dictionary<long, bool> _reachable = new();

    public EnvironmentHealthMonitor(IServiceScopeFactory scopeFactory, ILogger<EnvironmentHealthMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Environment health monitor tick failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var environments = scope.ServiceProvider.GetRequiredService<EnvironmentService>();
        var connection = scope.ServiceProvider.GetRequiredService<IEnvironmentConnectionService>();
        var bus = scope.ServiceProvider.GetRequiredService<IScheduleEventBus>();

        var envs = await environments.GetEnabledAsync(ct);
        var live = new HashSet<long>();

        foreach (var env in envs)
        {
            live.Add(env.Id);
            bool up;
            try
            {
                var result = await connection.TestConnectionAsync(environments.BuildSettings(env), ct);
                up = result.Success;
            }
            catch
            {
                up = false;
            }

            if (_reachable.TryGetValue(env.Id, out var prev) && prev && !up)
            {
                await bus.PublishAsync(ScheduleEvent.EnvironmentOffline, env.Id, env.Name, ct);
            }

            _reachable[env.Id] = up;
        }

        // Forget environments that are no longer enabled so re-enabling re-establishes a baseline.
        foreach (var stale in _reachable.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _reachable.Remove(stale);
        }
    }
}
