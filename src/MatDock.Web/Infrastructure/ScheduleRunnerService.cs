using MatDock.Core.Schedules;

namespace MatDock.Web.Infrastructure;

/// <summary>Background worker that runs due (cron) scheduled tasks once per minute.</summary>
public sealed class ScheduleRunnerService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduleRunnerService> _logger;

    public ScheduleRunnerService(IServiceScopeFactory scopeFactory, ILogger<ScheduleRunnerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations, seeding) settle first.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
                await schedules.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Schedule runner tick failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
