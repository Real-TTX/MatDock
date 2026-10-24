using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Schedules;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Reads CPU, memory and disk of every enabled, reachable environment every five minutes into <see cref="HostMetricsHistory"/>, and publishes
/// <see cref="ScheduleEvent.HostThreshold"/> when a host goes over one of its limits or comes back (<see cref="HostLimitWatch"/>).
/// </summary>
public sealed class HostMetricsSampler(IServiceScopeFactory scopes, HostMetricsHistory history, ILogger<HostMetricsSampler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly HostLimitWatch _limits = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var environments = scope.ServiceProvider.GetRequiredService<EnvironmentService>();
                var connection = scope.ServiceProvider.GetRequiredService<IEnvironmentConnectionService>();
                var bus = scope.ServiceProvider.GetRequiredService<IScheduleEventBus>();
                var settings = await scope.ServiceProvider.GetRequiredService<NotificationSettingsService>().GetAsync(stoppingToken);
                foreach (var env in await environments.GetEnabledAsync(stoppingToken))
                {
                    if (env.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error)
                    {
                        continue;
                    }

                    try
                    {
                        var stats = await connection.GetHostStatsAsync(environments.BuildSettings(env), stoppingToken);
                        history.Add(env.Id, stats);
                        foreach (var message in _limits.Check(env, stats, HostLimits.Of(settings, env)))
                        {
                            await bus.PublishAsync(ScheduleEvent.HostThreshold, env.Id, message, stoppingToken);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Host metrics of {Environment} not read.", env.Name);
                    }
                }
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
}
