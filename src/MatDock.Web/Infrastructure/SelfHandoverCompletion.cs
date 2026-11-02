using MatDock.Core.Execution;
using MatDock.Core.Stacks;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Gives the deploys of MatDock's own stack that a helper container finished (<see cref="SelfHandover"/>) their result in the history. After a start: the
/// helper writes it once compose is done, which may be a little after this MatDock started. After a handover in this process: when compose changed nothing,
/// MatDock was not restarted and reads the result itself. Either way it looks every few seconds for a few minutes.
/// </summary>
public sealed class SelfHandoverCompletion(IServiceScopeFactory scopes, SelfContainer self, ILogger<SelfHandoverCompletion> logger) : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    /// <summary>How long it looks after a start or a handover.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>A handed-over deploy older than this without a result ends as failed.</summary>
    public static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CompleteAsync(stoppingToken);
                await self.WaitForHandoverAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CompleteAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow + Window;
        while (!ct.IsCancellationRequested)
        {
            int waiting;
            try
            {
                using var scope = scopes.CreateScope();
                waiting = await scope.ServiceProvider.GetRequiredService<StackService>().CompleteHandoversAsync(GiveUp, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The result of a handed-over deploy could not be read.");
                waiting = 1;
            }

            if (waiting == 0 || DateTime.UtcNow > until)
            {
                return;
            }

            await Task.Delay(Poll, ct);
        }
    }
}
