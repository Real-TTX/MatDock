using System.ComponentModel;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Web.Api;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>The updates of all stacks at once: what the last look found, looking again, and updating every stack that has something.</summary>
[McpServerToolType]
public static class UpdateTools
{
    private const string EnvironmentsHelp = "Only this environment: id or name. Left out = every enabled environment.";
    private const string WaitHelp = "Seconds to wait for the end (0 to 600, default 120); still running = an operation id for get_operation.";

    [McpServerTool(Name = "list_updates", ReadOnly = true), Description("The update overview from the last look: every stack, managed by MatDock or only found on the host (managed=false), with its rule (mode, strategy), the services that have a newer image (updates), newer versions the rule does not take by itself (newerVersions), when it was looked at (checkedAt; none = never) and why MatDock cannot update it (blocked). Asks no registry: check_updates looks again.")]
    public static async Task<object> ListUpdates(EnvironmentService environments, UpdateOverview overview,
        [Description(EnvironmentsHelp)] string? environment = null,
        [Description("Only the stacks with something to update.")] bool onlyPending = false, CancellationToken ct = default)
    {
        var rows = await overview.RowsAsync(await EnvironmentsAsync(environments, environment, ct), ct);
        return rows.Where(r => !onlyPending || r.Pending > 0).Select(UpdateOverview.View).ToList();
    }

    [McpServerTool(Name = "check_updates", ReadOnly = true), Description("Looks for updates in every stack now, the managed ones and those only found on the host: asks each host once and each registry once per image, and changes nothing on the hosts. Answers the run and, once it ended, the stacks with something to update (as list_updates).")]
    public static async Task<object> CheckUpdates(EnvironmentService environments, UpdateOverview overview, OperationRegistry operations,
        [Description(EnvironmentsHelp)] string? environment = null,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var envs = await EnvironmentsAsync(environments, environment, ct);
        var operation = await UpdatesApi.StartCheckAsync(operations, overview, envs, ct);
        var run = await McpScope.WaitAsync(operations, operation, waitSeconds, ct);
        return new
        {
            check = run,
            pending = operation.IsFinished ? (await overview.RowsAsync(envs, ct)).Where(r => r.Pending > 0).Select(UpdateOverview.View).ToList() : null,
        };
    }

    [McpServerTool(Name = "apply_updates", ReadOnly = false), Description("Updates every stack the last look found something for (check_updates first), one after the other, each as its update rule says: the versions its strategy allows, a bundle backup first, the health check and the rollback. A stack MatDock did not deploy gets its moved tags pulled in place with its own compose files; one MatDock cannot update by itself is left out (list_updates says why). One stack alone: stack_action with update.")]
    public static async Task<object> ApplyUpdates(EnvironmentService environments, UpdateOverview overview, OperationRegistry operations,
        [Description(EnvironmentsHelp)] string? environment = null,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var envs = await EnvironmentsAsync(environments, environment, ct);
        var (operation, stacks) = await UpdatesApi.StartApplyAsync(operations, overview, envs, "mcp", ct);
        return operation is null
            ? (object)new { stacks = 0, message = UpdatesApi.NothingToUpdate }
            : new { stacks, update = await McpScope.WaitAsync(operations, operation, waitSeconds, ct) };
    }

    private static async Task<IReadOnlyCollection<DockerEnvironment>> EnvironmentsAsync(EnvironmentService environments, string? environment, CancellationToken ct)
        => string.IsNullOrWhiteSpace(environment) ? await environments.GetEnabledAsync(ct) : [await McpScope.EnvironmentAsync(environments, environment, ct)];
}
