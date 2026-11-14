using System.Globalization;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;

namespace MatDock.Web.Api;

/// <summary>
/// Looking for updates in many stacks and updating many, as <see cref="Operation"/>s: one place for the page (<c>/Updates</c>), the REST API
/// (<c>/api/v1/updates</c>) and the MCP tools. Managed stacks and the compose projects MatDock did not deploy (discovered ones) run in the same
/// operation, one after the other; two of these operations never overlap (<see cref="LockKey"/>).
/// </summary>
public static class UpdateOperations
{
    public const string LockKey = "updates";

    /// <summary>
    /// Looks for updates in the stacks of <paramref name="stackIds"/>, in every discovered project that runs on the hosts of
    /// <paramref name="projectHostIds"/> right now, and at the packages of the hosts of <paramref name="hostIds"/> (Docker itself). Every host is asked
    /// once for all its stacks and every registry once per image. The result is <c>{"pending": n}</c>: the stacks with something to update.
    /// </summary>
    public static Operation Check(OperationRegistry operations, IReadOnlyList<long> stackIds, IReadOnlyList<long> projectHostIds, IReadOnlyList<long> hostIds)
        => operations.Start("updates-check", "all", LockKey, async ctx =>
        {
            var ct = ctx.CancellationToken;
            var automation = ctx.Services.GetRequiredService<UpdateAutomation>();
            var updates = ctx.Services.GetRequiredService<UpdateService>();
            var environments = ctx.Services.GetRequiredService<EnvironmentService>();
            var batch = new UpdateCheckBatch(); // every host is asked once for all its stacks, the managed and the discovered ones
            var stacks = await StacksAsync(ctx, stackIds);

            // The projects MatDock did not deploy, as they run on the hosts right now.
            var projects = new List<(DockerEnvironment Env, string Project)>();
            foreach (var id in projectHostIds)
            {
                if (await environments.GetAsync(id, ct) is not { IsEnabled: true } env)
                {
                    continue;
                }

                try
                {
                    foreach (var project in await updates.DiscoveredProjectsAsync(env, ct, batch))
                    {
                        projects.Add((env, project));
                        ctx.Operation.Steps.Add(UpdateAutomation.ProjectStepKey(env.Id, project), project);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log($"{env.Name}: {ex.Message}");
                }
            }

            var hosts = await HostEnvironmentsAsync(ctx, hostIds);
            var pending = stacks.Count == 0 ? 0 : await automation.CheckStacksAsync(stacks, ctx.Log, ct, ctx.Operation.Steps, batch);
            pending += projects.Count == 0 ? 0 : await automation.CheckProjectsAsync(projects, ctx.Log, ct, ctx.Operation.Steps, batch);
            ctx.Log(pending == 0 ? "No stack to update." : $"{pending} stack(s) can be updated.");
            await CheckHostsAsync(ctx, hosts);
            return OperationOutcome.Success(new { pending });
        });

    /// <summary>
    /// Updates the stacks of <paramref name="stackIds"/> and the discovered <paramref name="projects"/> now, one after the other, whatever the mode of
    /// their rules: each is looked at again and gets what its rule makes of it, with the backup and the health gate of that rule. The result is
    /// <c>{"updated": n, "failed": n}</c>.
    /// </summary>
    /// <param name="target">What the run is named after: a stack, or "all".</param>
    /// <param name="trigger">Who started it, for the history of each stack: "manual", "api" or "mcp".</param>
    public static Operation Update(OperationRegistry operations, IReadOnlyList<long> stackIds, IReadOnlyList<(long EnvironmentId, string Project)> projects, string target,
        string trigger = "manual")
        => operations.Start("updates", target, LockKey, async ctx =>
        {
            var ct = ctx.CancellationToken;
            var automation = ctx.Services.GetRequiredService<UpdateAutomation>();
            var environments = ctx.Services.GetRequiredService<EnvironmentService>();
            var stacks = await StacksAsync(ctx, stackIds);
            var discovered = new List<(DockerEnvironment Env, string Project)>();
            foreach (var (environmentId, project) in projects)
            {
                if (await environments.GetAsync(environmentId, ct) is { IsEnabled: true } env)
                {
                    discovered.Add((env, project));
                    ctx.Operation.Steps.Add(UpdateAutomation.ProjectStepKey(env.Id, project), project);
                }
            }

            var (updated, failed) = stacks.Count == 0 ? (0, 0) : await automation.UpdateStacksAsync(stacks, ctx.Log, ct, ctx.Operation.Steps, trigger);
            if (discovered.Count > 0)
            {
                var (projectsUpdated, projectsFailed) = await automation.UpdateProjectsAsync(discovered, ctx.Log, ct, ctx.Operation.Steps);
                updated += projectsUpdated;
                failed += projectsFailed;
            }

            ctx.Log($"{updated} updated, {failed} failed.");
            var result = new { updated, failed };
            return failed == 0 ? OperationOutcome.Success(result) : OperationOutcome.Failure($"{failed} stack(s) failed.", result);
        });

    /// <summary>
    /// Updates one discovered project in place with its own compose files, as the rule of its environment says (backup, health gate, rollback); with
    /// <paramref name="backupFirst"/> a bundle backup is taken even when the rule asks for none. Under the lock of the stack, like every other action
    /// on it. The result is <c>{"changed": bool}</c>; a project MatDock cannot update by itself fails with the reason and is not touched.
    /// </summary>
    public static Operation ProjectUpdate(OperationRegistry operations, long environmentId, string name, bool backupFirst)
        => operations.Start("update", name, StackOperations.LockKey(environmentId, name), async ctx =>
        {
            var ct = ctx.CancellationToken;
            var env = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(environmentId, ct);
            if (env is null)
            {
                return OperationOutcome.Failure("Environment not found.", new { changed = false });
            }

            string? last = null;
            var (updated, failed) = await ctx.Services.GetRequiredService<UpdateAutomation>().UpdateProjectsAsync([(env, name)], line =>
            {
                last = line;
                ctx.Log(line);
            }, ct, backupFirst: backupFirst);
            if (failed > 0)
            {
                return OperationOutcome.Failure(StackOperations.LastLine(last), new { changed = false });
            }

            ctx.Log(updated > 0 ? "Containers were re-created with newer images." : "Nothing changed.");
            return OperationOutcome.Success(new { changed = updated > 0 });
        });

    private static async Task<List<Stack>> StacksAsync(OperationContext ctx, IEnumerable<long> ids)
    {
        var all = ctx.Services.GetRequiredService<StackService>();
        var list = new List<Stack>();
        foreach (var id in ids)
        {
            if (await all.GetAsync(id, ctx.CancellationToken) is { } stack)
            {
                list.Add(stack);
                ctx.Operation.Steps.Add(stack.Id.ToString(CultureInfo.InvariantCulture), stack.Name);
            }
        }

        return list;
    }

    /// <summary>The enabled environments of <paramref name="ids"/>, each as a step of the run (its host is looked at).</summary>
    public static async Task<List<DockerEnvironment>> HostEnvironmentsAsync(OperationContext ctx, IEnumerable<long> ids)
    {
        var environments = ctx.Services.GetRequiredService<EnvironmentService>();
        var list = new List<DockerEnvironment>();
        foreach (var id in ids)
        {
            if (await environments.GetAsync(id, ctx.CancellationToken) is { IsEnabled: true } env)
            {
                list.Add(env);
                ctx.Operation.Steps.Add($"host:{env.Id}", $"{env.Name} (host)");
            }
        }

        return list;
    }

    /// <summary>Looks at the packages of the hosts (refreshing their package lists; a minute per host is possible).</summary>
    public static async Task CheckHostsAsync(OperationContext ctx, IReadOnlyList<DockerEnvironment> envs)
    {
        var hosts = ctx.Services.GetRequiredService<HostUpdateService>();
        foreach (var env in envs)
        {
            var key = $"host:{env.Id}";
            ctx.Operation.Steps.Set(key, StepState.Running, "Refreshing the package lists");
            var report = await hosts.CheckAsync(env, refresh: true, ctx.CancellationToken);
            var summary = report.Summary(env.DockerVersion);
            ctx.Operation.Steps.Set(key, report.Error is null ? StepState.Done : StepState.Failed, summary);
            ctx.Log($"{env.Name} (host): {summary}");
        }
    }
}
