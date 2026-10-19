using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Reads the container events of every enabled environment once a minute and publishes
/// <see cref="ScheduleEvent.ContainerDied"/> and <see cref="ScheduleEvent.ContainerUnhealthy"/>, so an event schedule
/// (usually "Send a notification") can react. Asks the hosts only while such a schedule exists; the window of each
/// environment continues where the last one ended (at most <see cref="MaxWindow"/> back after an outage).
/// </summary>
public sealed class ContainerAlertMonitor : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(10);
    private static readonly ScheduleEvent[] Events = [ScheduleEvent.ContainerDied, ScheduleEvent.ContainerUnhealthy];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContainerAlertMonitor> _logger;
    private readonly Dictionary<long, long> _cursor = new();

    public ContainerAlertMonitor(IServiceScopeFactory scopeFactory, ILogger<ContainerAlertMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The window to read now: from the end of the last one (or <paramref name="until"/> for a new environment,
    /// which only sets the start), never further back than <see cref="MaxWindow"/>.</summary>
    public static (long Since, long Until) Window(long? lastUntil, long until)
        => (Math.Max(lastUntil ?? until, until - (long)MaxWindow.TotalSeconds), until);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _logger.LogWarning(ex, "Container alert monitor tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        var environments = scope.ServiceProvider.GetRequiredService<EnvironmentService>();
        var containers = scope.ServiceProvider.GetRequiredService<ContainerService>();
        var bus = scope.ServiceProvider.GetRequiredService<IScheduleEventBus>();

        // A few seconds back, so the events of the last moment are in the daemon's log when the window is read.
        var until = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
        var envs = await environments.GetEnabledAsync(ct);
        var wanted = await schedules.HasEventSchedulesAsync(Events, ct);

        foreach (var env in envs)
        {
            var (since, end) = Window(_cursor.TryGetValue(env.Id, out var last) ? last : null, until);
            if (!wanted || since >= end || env.Status is EnvironmentStatus.Offline)
            {
                _cursor[env.Id] = end;
                continue;
            }

            var alerts = await containers.ListAlertsAsync(env, since, end, ct);
            if (alerts is null)
            {
                continue; // host not reachable: read this window again next time
            }

            _cursor[env.Id] = end;
            foreach (var alert in alerts)
            {
                await bus.PublishAsync(alert.Event, env.Id, alert.Detail, ct);
            }
        }

        foreach (var gone in _cursor.Keys.Where(id => envs.All(e => e.Id != id)).ToList())
        {
            _cursor.Remove(gone);
        }
    }
}
