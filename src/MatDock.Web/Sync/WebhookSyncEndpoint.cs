using MatDock.Core.Sync;

namespace MatDock.Web.Sync;

/// <summary>
/// Anonymous incoming webhook that triggers a <see cref="SyncJob"/> by its per-job URL token
/// (e.g. a Git host's push webhook <c>POST /webhooks/sync/{token}</c>). The token in the path is the
/// only secret; it authenticates the caller and only ever triggers a sync (never returns data). The
/// actual clone+deploy runs in the background so the webhook is acknowledged immediately.
/// </summary>
public static class WebhookSyncEndpoint
{
    public static async Task HandleAsync(HttpContext context, string token)
    {
        var ct = context.RequestAborted;

        // Cheap shape check before hitting the DB.
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 16 or > 64)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var jobs = context.RequestServices.GetRequiredService<SyncJobService>();
        var job = await jobs.FindByWebhookTokenAsync(token, ct);
        if (job is null || !job.WebhookEnabled)
        {
            // Do not reveal whether the token exists but has the webhook trigger disabled.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Not found.");
            return;
        }

        var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebhookSync");
        var jobId = job.Id;

        // Fire-and-forget on a fresh DI scope: a clone + deploy can take a while.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SyncJobService>();
                var summary = await svc.RunNowAsync(jobId, force: false, CancellationToken.None);
                logger.LogInformation("Webhook sync {Id} finished: {Summary}", jobId, summary);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Webhook sync {Id} failed.", jobId);
            }
        });

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        await context.Response.WriteAsync("Sync triggered.");
    }
}
