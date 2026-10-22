using MatDock.Core.Updates;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Runs the update rules (<see cref="UpdateAutomation"/>) every quarter of an hour: each stack is looked at as often as its own rule says, so this only has to be
/// often enough for a time window to be hit. A minute after the start, so the app and its environments are up first.
/// </summary>
public sealed class UpdateAutomationService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpdateAutomationService> _logger;

    public UpdateAutomationService(IServiceScopeFactory scopeFactory, ILogger<UpdateAutomationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var report = await scope.ServiceProvider.GetRequiredService<UpdateAutomation>().RunAsync(DateTime.UtcNow, DateTime.Now, stoppingToken);
                    if (report.Lines.Count > 0)
                    {
                        _logger.LogInformation("Update automation: {Lines}", string.Join(" | ", report.Lines));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Update automation pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
