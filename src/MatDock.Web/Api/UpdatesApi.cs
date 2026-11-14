using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;

namespace MatDock.Web.Api;

/// <summary>
/// <c>/api/v1/updates</c>: the update overview of every stack (managed by MatDock or only found on the host), looking for updates in all of them and
/// updating all that have some. Unlike the other endpoints these work on every enabled environment when <c>?environment=</c> is left out. One stack
/// is updated with <c>POST /api/v1/stacks/{name}/actions/update</c>.
/// </summary>
public static class UpdatesApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/updates", ListAsync)
            .WithSummary("Every stack (managed or only found on the host) with its update rule and what the last look found; asks no registry. Without ?environment= every enabled environment.");
        api.MapPost("/updates/check", CheckAsync)
            .WithSummary("Looks for updates in every stack now (an operation; its result is {pending}). Without ?environment= every enabled environment.");
        api.MapPost("/updates/apply", ApplyAsync)
            .WithSummary("Updates every stack the last look found something for, each as its update rule says (an operation; its result is {updated, failed}). Without ?environment= every enabled environment.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, bool? pending, EnvironmentService environments, UpdateOverview overview)
    {
        var (envs, error) = await EnvironmentsAsync(http, environments);
        if (envs is null)
        {
            return error!;
        }

        var rows = await overview.RowsAsync(envs, http.RequestAborted);
        return ApiResults.Ok(rows.Where(r => pending != true || r.Pending > 0).Select(UpdateOverview.View));
    }

    private static async Task<IResult> CheckAsync(HttpContext http, EnvironmentService environments, UpdateOverview overview, OperationRegistry operations)
    {
        var (envs, error) = await EnvironmentsAsync(http, environments);
        if (envs is null)
        {
            return error!;
        }

        return ApiResults.Accepted(new { operationId = (await StartCheckAsync(operations, overview, envs, http.RequestAborted)).Id });
    }

    private static async Task<IResult> ApplyAsync(HttpContext http, EnvironmentService environments, UpdateOverview overview, OperationRegistry operations)
    {
        var (envs, error) = await EnvironmentsAsync(http, environments);
        if (envs is null)
        {
            return error!;
        }

        var (operation, count) = await StartApplyAsync(operations, overview, envs, "api", http.RequestAborted);
        return operation is null
            ? ApiResults.Ok(new { operationId = (string?)null, stacks = 0, message = NothingToUpdate })
            : ApiResults.Accepted(new { operationId = operation.Id, stacks = count });
    }

    public const string NothingToUpdate = "Nothing to update: the last look found no stack with a newer image that MatDock can update. Look again first (check).";

    /// <summary>A look for updates in every stack of <paramref name="environments"/>, the managed and the discovered ones (not at the packages of the hosts).</summary>
    public static async Task<Operation> StartCheckAsync(OperationRegistry operations, UpdateOverview overview, IReadOnlyCollection<DockerEnvironment> environments, CancellationToken ct)
    {
        var stacks = (await overview.StacksAsync(environments, ct)).Select(x => x.Stack.Id).ToList();
        return UpdateOperations.Check(operations, stacks, UpdateOverview.ProjectEnvironments(environments).Select(e => e.Id).ToList(), []);
    }

    /// <summary>
    /// The update of every stack of <paramref name="environments"/> the last look found something for; a discovered project MatDock could not update
    /// the last time is left out. No operation when there is none.
    /// </summary>
    public static async Task<(Operation? Operation, int Stacks)> StartApplyAsync(OperationRegistry operations, UpdateOverview overview,
        IReadOnlyCollection<DockerEnvironment> environments, string trigger, CancellationToken ct)
    {
        var rows = (await overview.RowsAsync(environments, ct)).Where(r => r.Applicable).ToList();
        if (rows.Count == 0)
        {
            return (null, 0);
        }

        var stacks = rows.Where(r => r.Stack is not null).Select(r => r.Stack!.Id).ToList();
        var projects = rows.Where(r => r.Stack is null).Select(r => (r.Environment.Id, r.Name)).ToList();
        return (UpdateOperations.Update(operations, stacks, projects, "all", trigger), rows.Count);
    }

    /// <summary>The environments of a request: the one <c>?environment=</c> names, without it every enabled one.</summary>
    private static async Task<(IReadOnlyCollection<DockerEnvironment>? Environments, IResult? Error)> EnvironmentsAsync(HttpContext http, EnvironmentService environments)
    {
        if (http.Request.Query["environment"].ToString().Trim().Length == 0)
        {
            return (await environments.GetEnabledAsync(http.RequestAborted), null);
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        return env is null ? (null, error) : ([env], null);
    }
}
