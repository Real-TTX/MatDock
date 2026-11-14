using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;

namespace MatDock.Web.Api;

/// <summary>
/// The long actions on a stack as <see cref="Operation"/>s, one place for the REST API (<c>/api/v1</c>) and the MCP tools (<c>/mcp</c>): deploy,
/// update, the lifecycle actions, delete, bundle backup and restore. Operations on one stack never overlap (<see cref="LockKey"/>).
/// </summary>
public static class StackOperations
{
    /// <summary>The lifecycle actions of a managed stack.</summary>
    public static readonly string[] Actions = ["start", "stop", "restart", "pull", "redeploy", "update"];

    /// <summary>What a compose project MatDock does not manage can do: by its label, and an update in place with its own compose files.</summary>
    public static readonly string[] ProjectActions = ["start", "stop", "restart", "update"];

    /// <summary>The answer to an action a project MatDock does not manage cannot do.</summary>
    public static string NotManaged(string name) => $"Stack '{name}' is not managed by MatDock; only {string.Join(", ", ProjectActions[..^1])} and {ProjectActions[^1]} are possible.";

    /// <summary>A delete waits for a running deploy of the same stack, and so on.</summary>
    public static string LockKey(long environmentId, string name) => $"stack:{environmentId}:{name}";

    /// <summary><c>docker compose up -d</c> (with <paramref name="pull"/>: pull first), recorded as started by the API.</summary>
    public static Operation Deploy(OperationRegistry operations, long environmentId, long stackId, string name, bool pull)
        => Deploy(operations, environmentId, stackId, name, new DeployRequest(Trigger: DeployTrigger.Api), pull);

    /// <summary>
    /// A deploy of a managed stack as an operation: with <paramref name="update"/> a pull and <c>up -d</c> (plus the options the request names), else
    /// <c>up -d</c> with the options of the request (null = the stack's own). Its trigger and actor go into the deploy history.
    /// </summary>
    public static Operation Deploy(OperationRegistry operations, long environmentId, long stackId, string name, DeployRequest request, bool update = false)
        => operations.Start("deploy", name, LockKey(environmentId, name), async ctx =>
        {
            var stacks = ctx.Services.GetRequiredService<StackService>();
            ctx.Log(update ? $"==> Updating {name}: docker compose pull && docker compose up -d" : $"==> Deploying {name}");
            var (ok, output) = update
                ? await stacks.UpdateAsync(stackId, request, ctx.CancellationToken)
                : await stacks.DeployAsync(stackId, request, ctx.CancellationToken);
            return ok ? OperationOutcome.Success() : OperationOutcome.Failure($"Deploy failed: {LastLine(output)}");
        });

    /// <summary>
    /// <c>update</c>, as the update rule of the stack says and as its page does it: a look at the registries, a newer version its strategy takes is set
    /// as the image MatDock runs, then the pull and the redeploy (also when nothing new is known: re-creating only what changed), and with the rule's
    /// rollback the health check, which puts the old images back when the containers do not come up healthy. The bundle backup is the caller's choice
    /// (<paramref name="backupFirst"/>), not the rule's. The result is <c>{"changed": bool, "rolledBack": bool}</c>.
    /// </summary>
    public static Operation Update(OperationRegistry operations, long environmentId, long stackId, string name, bool backupFirst, string trigger)
        => operations.Start("update", name, LockKey(environmentId, name), async ctx =>
        {
            var ct = ctx.CancellationToken;
            var updates = ctx.Services.GetRequiredService<UpdateService>();
            var env = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(environmentId, ct);
            var stack = await ctx.Services.GetRequiredService<StackService>().GetAsync(stackId, ct);
            if (env is null || stack is null)
            {
                return OperationOutcome.Failure(env is null ? "Environment not found." : "Stack not found.", new { changed = false, rolledBack = false });
            }

            ctx.Log("==> Checking the registries for newer images");
            var policy = (await ctx.Services.GetRequiredService<UpdatePolicyService>().LoadAsync(ct)).For(stack) with { PreBackup = backupFirst };
            var plan = await updates.PlanStackAsync(env, stack, policy, ct);
            if (plan.Error is not null)
            {
                ctx.Log($"    (check failed: {plan.Error})");
            }

            var planned = plan.Items.ToDictionary(i => i.Service, StringComparer.Ordinal);
            foreach (var image in ctx.Services.GetRequiredService<UpdateCheckCache>().Report(env.Id, name)?.Report.Images ?? [])
            {
                var state = planned.TryGetValue(image.Service, out var item)
                    ? item.Kind == PlannedKind.Version ? $"→ {item.Target}" : "newer image available"
                    : image.Note ?? "up to date";
                ctx.Log($"    {image.Service}: {image.Image} ({state})");
            }

            var outcome = await ctx.Services.GetRequiredService<UpdateAutomation>().ApplyAsync(stack, plan.Items, policy, ct, trigger, ctx.Log);
            if (!outcome.Ok)
            {
                return OperationOutcome.Failure(LastLine(outcome.Message), new { changed = false, rolledBack = outcome.RolledBack });
            }

            ctx.Log(outcome.Changed ? "Containers were re-created with newer images." : "Nothing changed.");
            return OperationOutcome.Success(new { changed = outcome.Changed, rolledBack = false });
        });

    /// <summary>start, stop, restart, pull or redeploy of a managed stack (update has its own, <see cref="Update"/>).</summary>
    public static Operation Action(OperationRegistry operations, long environmentId, long stackId, string name, string action, DeployRequest? request = null)
        => operations.Start(action, name, LockKey(environmentId, name), async ctx =>
        {
            var svc = ctx.Services.GetRequiredService<StackService>();
            var ct = ctx.CancellationToken;
            ctx.Log(action == "redeploy" ? "==> docker compose up -d" : $"==> docker compose {action}");
            var (ok, output) = action switch
            {
                "start" => await svc.StartAsync(stackId, ct),
                "stop" => await svc.StopAsync(stackId, ct),
                "restart" => await svc.RestartAsync(stackId, ct),
                "pull" => await svc.PullAsync(stackId, ct),
                _ => await svc.DeployAsync(stackId, request ?? new DeployRequest(Trigger: DeployTrigger.Api), ct),
            };
            return ok ? OperationOutcome.Success() : OperationOutcome.Failure($"{action} failed: {LastLine(output)}");
        });

    /// <summary>start, stop or restart of a compose project MatDock does not manage (found by its label).</summary>
    public static Operation ProjectAction(OperationRegistry operations, long environmentId, string name, string action)
        => operations.Start(action, name, LockKey(environmentId, name), async ctx =>
        {
            var target = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(environmentId, ctx.CancellationToken);
            if (target is null)
            {
                return OperationOutcome.Failure("Environment not found.");
            }

            var svc = ctx.Services.GetRequiredService<StackService>();
            var (ok, output) = action switch
            {
                "start" => await svc.ProjectStartAsync(target, name, ctx.CancellationToken),
                "stop" => await svc.ProjectStopAsync(target, name, ctx.CancellationToken),
                _ => await svc.ProjectRestartAsync(target, name, ctx.CancellationToken),
            };
            return ok ? OperationOutcome.Success() : OperationOutcome.Failure($"{action} failed: {LastLine(output)}");
        });

    /// <summary><c>compose down</c> (with <paramref name="removeVolumes"/>: <c>--volumes</c>), then the stack is removed from MatDock.</summary>
    public static Operation Delete(OperationRegistry operations, long environmentId, long stackId, string name, bool removeVolumes)
        => operations.Start("delete", name, LockKey(environmentId, name), async ctx =>
        {
            var svc = ctx.Services.GetRequiredService<StackService>();
            ctx.Log(removeVolumes ? "==> docker compose down --volumes" : "==> docker compose down");
            var (ok, output) = await svc.DownAsync(stackId, removeVolumes, ctx.CancellationToken);
            if (!ok)
            {
                return OperationOutcome.Failure($"compose down failed, the stack is kept: {LastLine(output)}");
            }

            await svc.DeleteAsync(stackId, ctx.CancellationToken);
            ctx.Log("Stack removed from MatDock.");
            return OperationOutcome.Success(new { removed = true, volumesRemoved = removeVolumes });
        });

    /// <summary>A bundle backup (volumes, compose, .env, image references) of a stack to a target (null = local); the result is <c>{"backupId"}</c>.</summary>
    public static Operation Backup(OperationRegistry operations, long environmentId, string name, long? targetId, bool stopContainers, string? note)
        => operations.Start("backup", name, LockKey(environmentId, name), async ctx =>
        {
            var ct = ctx.CancellationToken;
            var target = targetId is { } id ? await ctx.Services.GetRequiredService<BackupTargetService>().GetAsync(id, ct) : null;
            var environment = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(environmentId, ct);
            if (environment is null)
            {
                return OperationOutcome.Failure("Environment not found.");
            }

            ctx.Log($"==> Bundle backup of {name} to {target?.Name ?? "Local"}{(stopContainers ? " (containers stopped)" : string.Empty)}");
            var result = await ctx.Services.GetRequiredService<BundleBackupService>().CreateBundleAsync(
                environment, name, target, stopContainers, StackBackupKind.Manual, note, ct: ct);
            ctx.Log(result.Message);
            return result.Ok
                ? OperationOutcome.Success(new { backupId = result.BackupId })
                : OperationOutcome.Failure(result.Message, new { backupId = result.BackupId });
        });

    /// <summary>Restores a bundle (re-creating the stack when it is gone); with <paramref name="backupCurrentFirst"/> a safety bundle of the current state first.</summary>
    public static Operation Restore(OperationRegistry operations, long environmentId, StackBackup record, bool backupCurrentFirst)
    {
        var id = record.Id;
        return operations.Start("restore", record.StackName, LockKey(environmentId, record.StackName), async ctx =>
        {
            var ct = ctx.CancellationToken;
            var environment = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(environmentId, ct);
            if (environment is null)
            {
                return OperationOutcome.Failure("Environment not found.");
            }

            var (ok, message, safetyId) = await ctx.Services.GetRequiredService<BundleBackupService>()
                .RestoreAsync(id, environment, backupCurrentFirst, ctx.Log, ct);
            ctx.Log(message);
            var result = new { backupId = id, safetyBackupId = safetyId };
            return ok ? OperationOutcome.Success(result) : OperationOutcome.Failure(message, result);
        });
    }

    /// <summary>The last non-empty line of a command output (compose prints its error last).</summary>
    public static string LastLine(string? output)
    {
        var line = (output ?? string.Empty).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return line ?? "unknown error";
    }
}
