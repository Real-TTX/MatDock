using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;

namespace MatDock.Web.Infrastructure;

/// <summary>Reads CPU, memory and disk of every enabled, reachable environment every five minutes into <see cref="HostMetricsHistory"/>.</summary>
public sealed class HostMetricsSampler(IServiceScopeFactory scopes, HostMetricsHistory history, ILogger<HostMetricsSampler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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
                foreach (var env in await environments.GetEnabledAsync(stoppingToken))
                {
                    if (env.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error)
                    {
                        continue;
                    }

                    try
                    {
                        history.Add(env.Id, await connection.GetHostStatsAsync(environments.BuildSettings(env), stoppingToken));
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
