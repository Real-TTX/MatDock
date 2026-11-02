using System.Security.Cryptography;
using System.Text;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;

namespace MatDock.Web.Sync;

/// <summary>
/// The webhook of a stack (<c>POST /webhooks/stacks/{token}</c>). For a Git stack it is the push hook of the repository: MatDock asks for the newest commit
/// and answers <c>skipped</c> at once when there is none; otherwise a sync runs (it deploys only when the files of the compose folder changed). For any other
/// stack a CI calls it after it pushed a new image: MatDock pulls and redeploys with the backup and the health gate of the stack's update rule.
/// The token in the path is the secret. When the caller signs the body (<c>X-Hub-Signature-256</c> of GitHub, Gitea and Forgejo, with the token as the
/// secret) or names a token (<c>X-Gitlab-Token</c>), that must match too. The answer is the id of the run, never data.
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

        if (await SignatureProblemAsync(context.Request, token) is { } problem)
        {
            return Results.Json(new { error = problem }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var stackId = stack.Id;
        var lockKey = MatDock.Web.Api.StacksApi.LockKey(stack.EnvironmentId, stack.Name);
        if (stack.IsGitBacked)
        {
            var head = await stacks.RemoteHeadAsync(stackId, context.RequestAborted);
            if (head is not null && string.Equals(head, stack.GitLastCommit, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new { status = "skipped", stack = stack.Name, message = "No new commits." });
            }

            var sync = operations.Start("webhook-sync", stack.Name, lockKey, async ctx =>
            {
                var result = await ctx.Services.GetRequiredService<StackService>()
                    .SyncGitAsync(stackId, new DeployRequest(Trigger: DeployTrigger.Webhook, Actor: "Webhook"), ct: ctx.CancellationToken);
                ctx.Log(GitSyncAction.Describe(result));
                return result.Outcome == GitSyncOutcome.Failed
                    ? OperationOutcome.Failure("The sync failed.")
                    : OperationOutcome.Success(new { status = result.Outcome.ToString().ToLowerInvariant(), commit = result.Commit });
            });
            return Results.Accepted(value: new { status = "started", operationId = sync.Id, stack = stack.Name });
        }

        var policy = await policies.ResolveAsync(stack, context.RequestAborted);
        var operation = operations.Start("webhook-update", stack.Name, lockKey, async ctx =>
        {
            var outcome = await ctx.Services.GetRequiredService<UpdateService>().RunUpdateAsync(stackId, policy.PreBackup,
                policy.Rollback ? UpdateSafety.Default : null, ctx.CancellationToken, ctx.Log, trigger: "webhook");
            ctx.Log(outcome.Message);
            return outcome.Ok ? OperationOutcome.Success(new { changed = outcome.Changed }) : OperationOutcome.Failure(outcome.RolledBack ? "Rolled back." : "The update failed.");
        });

        return Results.Accepted(value: new { operationId = operation.Id, stack = stack.Name });
    }

    /// <summary>Why a signed or token-carrying call is refused, or null (no signature and no token header is fine: the path carries the secret).</summary>
    internal static async Task<string?> SignatureProblemAsync(HttpRequest request, string token)
    {
        if (request.Headers.TryGetValue("X-Gitlab-Token", out var gitlab) && gitlab.Count > 0)
        {
            return Equal(gitlab.ToString(), token) ? null : "The X-Gitlab-Token does not match.";
        }

        if (!request.Headers.TryGetValue("X-Hub-Signature-256", out var signature) || signature.Count == 0)
        {
            return null;
        }

        var text = signature.ToString();
        if (!text.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return "The signature is not sha256=<hex>.";
        }

        request.EnableBuffering();
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
        request.Body.Position = 0;
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), buffer.ToArray())).ToLowerInvariant();
        return Equal(text.ToLowerInvariant(), expected) ? null : "The signature (X-Hub-Signature-256) does not match: set the webhook's secret to the token of the address.";
    }

    private static bool Equal(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
