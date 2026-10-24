using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;

namespace MatDock.Web.Sync;

/// <summary>
/// The redeploy webhook of a stack (<c>POST /webhooks/stacks/{token}</c>): a CI calls it after it pushed a new image, MatDock pulls and redeploys the stack
/// with the backup and the health gate of the stack's update rule. The token in the path is the only secret; the answer is the id of the run, never data.
/// </summary>
public static class WebhookStackEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string token, StackService stacks, UpdatePolicyService policies, OperationRegistry operations)
    {
        var stack = await stacks.GetByWebhookTokenAsync(token, context.RequestAborted);
        if (stack is null)
        {
            return Results.NotFound(new { error = "Not found." });
        }

        var policy = await policies.ResolveAsync(stack, context.RequestAborted);
        var stackId = stack.Id;
        var operation = operations.Start("webhook-update", stack.Name, MatDock.Web.Api.StacksApi.LockKey(stack.EnvironmentId, stack.Name), async ctx =>
        {
            var outcome = await ctx.Services.GetRequiredService<UpdateService>().RunUpdateAsync(stackId, policy.PreBackup,
                policy.Rollback ? UpdateSafety.Default : null, ctx.CancellationToken, ctx.Log, trigger: "webhook");
            ctx.Log(outcome.Message);
            return outcome.Ok ? OperationOutcome.Success(new { changed = outcome.Changed }) : OperationOutcome.Failure(outcome.RolledBack ? "Rolled back." : "The update failed.");
        });

        return Results.Accepted(value: new { operationId = operation.Id, stack = stack.Name });
    }
}
