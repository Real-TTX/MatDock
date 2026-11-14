using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Web.Api;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Updates;

/// <summary>
/// All stacks (managed and discovered) with their update rule and what the rule would update, from the last look; checking and updating many at once.
/// The rows and the runs are those of the API and the MCP tools (<see cref="UpdateOverview"/>, <see cref="UpdateOperations"/>).
/// </summary>
public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<UpdateRow>> SortChoices = ListView.Sorts<UpdateRow>(
        ("Environment", r => r.EnvName), ("Stack", r => r.Name), ("Updates", r => r.Pending), ("Checked", r => r.CheckedUtc));

    private readonly EnvironmentService _environments;
    private readonly UpdateOverview _overview;
    private readonly OperationRegistry _operations;
    private readonly HostUpdateCache _hostCache;

    public IndexModel(EnvironmentService environments, UpdateOverview overview, OperationRegistry operations, HostUpdateCache hostCache)
    {
        _hostCache = hostCache;
        _environments = environments;
        _overview = overview;
        _operations = operations;
    }

    [BindProperty(SupportsGet = true)] public string? Op { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortKey { get; set; }

    public Operation? Running { get; private set; }
    public long EnvId { get; private set; }
    public List<UpdateRow> Rows { get; private set; } = new();

    /// <summary>The environment of the sidebar choice when it is one, for the link to its rule.</summary>
    public string? EnvName { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    /// <summary>
    /// One row of the overview: a managed stack (<paramref name="Stack"/>), or a compose project MatDock did not deploy (discovered; the rule of its
    /// environment applies and only a moved tag is updated). <paramref name="Hints"/>: newer versions the rule does not take (a strategy that follows
    /// the tag, or a discovered project). <paramref name="Blocked"/>: why MatDock could not update a discovered project the last time.
    /// </summary>
    public sealed record UpdateRow(Stack? Stack, long EnvId, string Name, string EnvName, EffectiveUpdatePolicy Policy, StackUpdatePlan? Plan, DateTime? CheckedUtc,
        IReadOnlyList<(string Service, string Tag)> Hints, string? Blocked = null)
    {
        public int Pending => Plan?.Items.Count ?? 0;
    }

    /// <summary>All the stacks of the chosen environment (the list may be narrowed by a search): what Update all would update.</summary>
    public int Total { get; private set; }

    public int PendingStacks { get; private set; }

    /// <summary>The hosts of the environments shown, with what apt would upgrade on them (Docker first of all), from the last look.</summary>
    public List<HostRow> HostRows { get; private set; } = new();

    public sealed record HostRow(DockerEnvironment Env, HostUpdateReport? Report, DateTime? CheckedUtc)
    {
        public bool DockerPending => Report?.DockerTarget is not null;
    }

    public ListToolbarModel Toolbar { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Running = string.IsNullOrEmpty(Op) ? null : _operations.Get(Op);
        await LoadAsync();
    }

    /// <summary>The environments shown: the sidebar choice when it is one, else every enabled one.</summary>
    private async Task<List<DockerEnvironment>> VisibleEnvironmentsAsync()
    {
        var envs = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        var chosen = EnvSelection.Current(HttpContext) is { } id ? envs.FirstOrDefault(e => e.Id == id) : null;
        EnvId = chosen?.Id ?? 0;
        EnvName = chosen?.Name;
        return chosen is null ? envs : [chosen];
    }

    private async Task<List<(Stack Stack, DockerEnvironment Env)>> VisibleStacksAsync()
        => await _overview.StacksAsync(await VisibleEnvironmentsAsync(), HttpContext.RequestAborted);

    /// <summary>One discovered project of the hosts shown by its environment and name, or null (not there, a managed stack, a bridge).</summary>
    private async Task<(DockerEnvironment Env, string Project)?> VisibleProjectAsync(long envId, string? project)
    {
        var found = (await _overview.ProjectsAsync(await VisibleEnvironmentsAsync(), HttpContext.RequestAborted))
            .FirstOrDefault(p => p.Env.Id == envId && string.Equals(p.Project, project, StringComparison.Ordinal));
        return found.Env is null ? null : found;
    }

    private async Task LoadAsync()
    {
        var all = (await _overview.RowsAsync(await VisibleEnvironmentsAsync(), HttpContext.RequestAborted))
            .Select(r => new UpdateRow(r.Stack, r.Environment.Id, r.Name, r.Environment.Name, r.Policy, r.Plan, r.CheckedUtc, r.Hints, r.Blocked))
            .ToList();
        Total = all.Count;
        PendingStacks = all.Count(r => r.Pending > 0 && r.Blocked is null); // what "Update all" would run: not what MatDock cannot update by itself
        Rows = ListView.Apply(all, Q, SortChoices, SortKey, r => r.Name, r => r.EnvName, r => r.Policy.Mode.ToString(),
            r => string.Join(" ", r.Plan?.Items.Select(i => i.Service) ?? []));
        Toolbar = ListView.Toolbar(Q, SortKey, "Search stacks", SortChoices);

        HostRows = (await VisibleHostsAsync())
            .Select(env => _hostCache.Get(env.Id) is { } cached ? new HostRow(env, cached.Report, cached.CheckedUtc) : new HostRow(env, null, null))
            .ToList();
    }

    /// <summary>The environments shown (the sidebar choice, or all) whose host MatDock can look at.</summary>
    private async Task<List<DockerEnvironment>> VisibleHostsAsync()
        => (await VisibleEnvironmentsAsync()).Where(HostUpdateService.Supports).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Looks for updates in every stack (the discovered ones too) and on every host shown (in the background: one after the other).</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var envs = await VisibleEnvironmentsAsync();
        var stacks = (await _overview.StacksAsync(envs, HttpContext.RequestAborted)).Select(x => x.Stack.Id).ToList();
        var hosts = envs.Where(HostUpdateService.Supports).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Id).ToList();
        var projectHosts = UpdateOverview.ProjectEnvironments(envs).Select(e => e.Id).ToList();
        if (stacks.Count == 0 && hosts.Count == 0 && projectHosts.Count == 0)
        {
            return RedirectToPage(new { Q, SortKey });
        }

        return RedirectToPage(new { op = UpdateOperations.Check(_operations, stacks, projectHosts, hosts).Id, Q, SortKey });
    }

    /// <summary>Looks at one host again (refreshing its package lists; in the background, it may take a minute).</summary>
    public async Task<IActionResult> OnPostCheckHostAsync(long id)
    {
        var env = (await VisibleHostsAsync()).FirstOrDefault(e => e.Id == id);
        if (env is null)
        {
            return RedirectToPage(new { Q, SortKey });
        }

        var operation = _operations.Start("updates-check", env.Name, $"host-update:{env.Id}", async ctx =>
        {
            await UpdateOperations.CheckHostsAsync(ctx, await UpdateOperations.HostEnvironmentsAsync(ctx, [env.Id]));
            return OperationOutcome.Success();
        });
        return RedirectToPage(new { op = operation.Id, Q, SortKey });
    }

    /// <summary>
    /// Upgrades the Docker packages of one host that the last look found (engine, CLI, containerd, plugins). The upgrade runs on the host
    /// itself; at its end Docker restarts and with it every container on that host.
    /// </summary>
    public async Task<IActionResult> OnPostUpgradeDockerAsync(long id)
    {
        var env = (await VisibleHostsAsync()).FirstOrDefault(e => e.Id == id);
        if (env is null || _hostCache.Get(env.Id) is not { Report.DockerTarget: not null })
        {
            StatusMessage = "Look at the host first: there is no Docker update known for it.";
            IsError = true;
            return RedirectToPage(new { Q, SortKey });
        }

        var operation = _operations.Start("host-upgrade", env.Name, $"host-update:{env.Id}", async ctx =>
        {
            var hosts = ctx.Services.GetRequiredService<HostUpdateService>();
            var current = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(env.Id, ctx.CancellationToken);
            if (current is null)
            {
                return OperationOutcome.Failure("The environment is gone.");
            }

            var packages = hosts.PendingDockerPackages(current.Id);
            if (packages.Count == 0)
            {
                return OperationOutcome.Failure("No Docker update is known for this host any more.");
            }

            ctx.Operation.Steps.Add("start", "Start the upgrade on the host");
            ctx.Operation.Steps.Add("apt", "apt-get: " + string.Join(", ", packages));
            ctx.Operation.Steps.Add("check", "Look at the host again");

            ctx.Operation.Steps.Set("start", StepState.Running);
            var unit = await hosts.StartUpgradeAsync(current, packages, ctx.CancellationToken);
            ctx.Operation.Steps.Set("start", StepState.Done, unit);
            ctx.Log($"The upgrade runs on {current.Name} as the systemd unit {unit} (log: {HostUpdateCommands.LogDirectory}/{unit}.log).");
            ctx.Log("At its end Docker restarts: every container on this host restarts with it.");
            if (current.ConnectionType == ConnectionType.Local)
            {
                ctx.Log("MatDock runs on this host too: it restarts with Docker and this page loses the run. The upgrade goes on regardless; look at the host again afterwards.");
            }

            ctx.Operation.Steps.Set("apt", StepState.Running);
            var (exit, log) = await WaitForUpgradeAsync(ctx, hosts, current, unit);
            if (exit != 0)
            {
                ctx.Operation.Steps.Set("apt", StepState.Failed, exit is null ? "no answer" : $"exit code {exit}");
                ctx.Log(log);
                return OperationOutcome.Failure(exit is null
                    ? $"The upgrade did not report back in time; see {HostUpdateCommands.LogDirectory}/{unit}.log on the host."
                    : $"apt-get ended with exit code {exit}.");
            }

            ctx.Operation.Steps.Set("apt", StepState.Done);
            ctx.Operation.Steps.Set("check", StepState.Running);
            var report = await hosts.CheckAsync(current, refresh: false, ctx.CancellationToken);
            ctx.Operation.Steps.Set("check", report.Error is null ? StepState.Done : StepState.Failed, report.Summary(null));
            ctx.Log($"{current.Name}: {report.Summary(null)}");
            return OperationOutcome.Success();
        });
        return RedirectToPage(new { op = operation.Id, Q, SortKey });
    }

    /// <summary>Follows the upgrade's log until it reports its exit code (at most half an hour). While Docker restarts the host may not answer for a moment.</summary>
    private static async Task<(int? Exit, string Log)> WaitForUpgradeAsync(OperationContext ctx, HostUpdateService hosts, DockerEnvironment env, string unit)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        var shown = 0;
        var log = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ctx.CancellationToken);
            int? exit;
            try
            {
                (exit, log) = await hosts.ReadUpgradeAsync(env, unit, ctx.CancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Log($"No answer from the host yet ({ex.Message}); Docker may be restarting.");
                continue;
            }

            var lines = log.Split('\n');
            if (lines.Length > shown)
            {
                foreach (var line in lines.Skip(shown).Where(l => l.Length > 0))
                {
                    ctx.Log(line);
                }

                shown = lines.Length;
            }

            if (exit is not null)
            {
                return (exit, string.Empty);
            }
        }

        return (null, log);
    }

    /// <summary>Updates the stacks shown that have something to update, one after the other, each with the backup and the health gate of its rule.</summary>
    public async Task<IActionResult> OnPostUpdateAllAsync()
    {
        var (operation, _) = await UpdatesApi.StartApplyAsync(_operations, _overview, await VisibleEnvironmentsAsync(), "manual", HttpContext.RequestAborted);
        return operation is null ? RedirectToPage(new { Q, SortKey }) : RedirectToPage(new { op = operation.Id, Q, SortKey });
    }

    /// <summary>Updates one stack the same way.</summary>
    public async Task<IActionResult> OnPostUpdateOneAsync(long id)
    {
        var stack = (await VisibleStacksAsync()).FirstOrDefault(x => x.Stack.Id == id).Stack;
        return stack is null ? RedirectToPage(new { Q, SortKey }) : StartUpdate([stack.Id], [], stack.Name);
    }

    /// <summary>Updates one discovered project in place, with its own compose files and the rule of its environment.</summary>
    public async Task<IActionResult> OnPostUpdateProjectAsync(long envId, string? project)
        => await VisibleProjectAsync(envId, project) is { } found ? StartUpdate([], [(found.Env.Id, found.Project)], found.Project) : RedirectToPage(new { Q, SortKey });

    /// <summary>Looks for updates in one stack (quick enough to wait for).</summary>
    public async Task<IActionResult> OnPostCheckOneAsync(long id)
    {
        var found = (await VisibleStacksAsync()).FirstOrDefault(x => x.Stack.Id == id);
        if (found.Stack is null)
        {
            return RedirectToPage(new { Q, SortKey });
        }

        var lines = new List<string>();
        await HttpContext.RequestServices.GetRequiredService<UpdateAutomation>().CheckStacksAsync([found.Stack], lines.Add, HttpContext.RequestAborted);
        StatusMessage = string.Join(" ", lines);
        IsError = false;
        return RedirectToPage(new { Q, SortKey });
    }

    /// <summary>Looks for updates in one discovered project.</summary>
    public async Task<IActionResult> OnPostCheckProjectAsync(long envId, string? project)
    {
        if (await VisibleProjectAsync(envId, project) is not { } found)
        {
            return RedirectToPage(new { Q, SortKey });
        }

        var lines = new List<string>();
        await HttpContext.RequestServices.GetRequiredService<UpdateAutomation>().CheckProjectsAsync([found], lines.Add, HttpContext.RequestAborted);
        StatusMessage = string.Join(" ", lines);
        IsError = false;
        return RedirectToPage(new { Q, SortKey });
    }

    private IActionResult StartUpdate(List<long> ids, List<(long EnvId, string Project)> projects, string target)
        => RedirectToPage(new { op = UpdateOperations.Update(_operations, ids, projects, target).Id, Q, SortKey });
}
