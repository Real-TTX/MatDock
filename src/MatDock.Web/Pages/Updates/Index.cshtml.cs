using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Updates;

/// <summary>All managed stacks with their update rule and what the rule would update, from the last look; checking and updating many at once.</summary>
public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<UpdateRow>> SortChoices = ListView.Sorts<UpdateRow>(
        ("Environment", r => r.EnvName), ("Stack", r => r.Name), ("Updates", r => r.Pending), ("Checked", r => r.CheckedUtc));

    private readonly EnvironmentService _environments;
    private readonly StackService _stacks;
    private readonly UpdatePolicyService _policies;
    private readonly UpdateCheckCache _cache;
    private readonly OperationRegistry _operations;
    private readonly HostUpdateCache _hostCache;
    private readonly HostReadings _readings;

    public IndexModel(EnvironmentService environments, StackService stacks, UpdatePolicyService policies, UpdateCheckCache cache, OperationRegistry operations,
        HostUpdateCache hostCache, HostReadings readings)
    {
        _hostCache = hostCache;
        _readings = readings;
        _environments = environments;
        _stacks = stacks;
        _policies = policies;
        _cache = cache;
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

    private async Task<List<(Stack Stack, DockerEnvironment Env)>> VisibleStacksAsync()
    {
        var ct = HttpContext.RequestAborted;
        var selected = EnvSelection.Current(HttpContext);
        var envs = (await _environments.GetEnabledAsync(ct)).ToDictionary(e => e.Id);
        EnvId = selected is { } id && envs.ContainsKey(id) ? id : 0;
        EnvName = EnvId > 0 ? envs[EnvId].Name : null;
        return (await _stacks.GetAllAsync(ct))
            .Where(s => envs.ContainsKey(s.EnvironmentId) && (EnvId == 0 || s.EnvironmentId == EnvId))
            .OrderBy(s => envs[s.EnvironmentId].Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => (s, envs[s.EnvironmentId]))
            .ToList();
    }

    /// <summary>The environments shown whose compose projects MatDock can look at itself (a bridge looks at its own).</summary>
    private async Task<List<DockerEnvironment>> ProjectEnvironmentsAsync()
    {
        var envs = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        var selected = EnvSelection.Current(HttpContext) is { } id && envs.Any(e => e.Id == id) ? id : 0;
        return envs
            .Where(e => e.ConnectionType != ConnectionType.Bridge && (selected == 0 || e.Id == selected))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The compose projects on the hosts shown that MatDock did not deploy. From the container list the dashboard reads too (the last one while a new
    /// one is taken), so the overview does not wait for every host; a host that does not answer has none here.
    /// </summary>
    private async Task<List<(DockerEnvironment Env, string Project)>> VisibleProjectsAsync(IEnumerable<(Stack Stack, DockerEnvironment Env)> stacks)
    {
        var ct = HttpContext.RequestAborted;
        var managed = stacks.ToLookup(x => x.Env.Id, x => x.Stack.Name);
        var lists = await Task.WhenAll((await ProjectEnvironmentsAsync()).Select(async env => (env, containers: await _readings.ContainersAsync(env, ct))));
        return lists
            .SelectMany(l => UpdateService.DiscoveredProjects(l.containers, managed[l.env.Id].ToHashSet(StringComparer.Ordinal)).Select(project => (l.env, project)))
            .ToList();
    }

    /// <summary>One discovered project of the hosts shown by its environment and name, or null (not there, a managed stack, a bridge).</summary>
    private async Task<(DockerEnvironment Env, string Project)?> VisibleProjectAsync(long envId, string? project)
    {
        var found = (await VisibleProjectsAsync(await VisibleStacksAsync())).FirstOrDefault(p => p.Env.Id == envId && string.Equals(p.Project, project, StringComparison.Ordinal));
        return found.Env is null ? null : found;
    }

    private async Task LoadAsync()
    {
        var policies = await _policies.LoadAsync(HttpContext.RequestAborted);
        var stacks = await VisibleStacksAsync();
        var all = stacks
            .Select(x =>
            {
                var cached = _cache.Plan(x.Env.Id, x.Stack.Name);
                return new UpdateRow(x.Stack, x.Env.Id, x.Stack.Name, x.Env.Name, policies.For(x.Stack), cached?.Plan, cached?.CheckedUtc, Hints(x.Env.Id, x.Stack.Name, cached?.Plan));
            })
            .Concat((await VisibleProjectsAsync(stacks)).Select(p =>
            {
                var cached = _cache.Plan(p.Env.Id, p.Project);
                return new UpdateRow(null, p.Env.Id, p.Project, p.Env.Name, policies.Inherited(UpdatePolicyScope.Stack, p.Env.Id), cached?.Plan, cached?.CheckedUtc,
                    Hints(p.Env.Id, p.Project, cached?.Plan), _cache.Blocked(p.Env.Id, p.Project));
            }))
            .OrderBy(r => r.EnvName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal)
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

    /// <summary>The newer versions the last look found that the plan does not take: said beside the row, nothing happens by itself.</summary>
    private IReadOnlyList<(string Service, string Tag)> Hints(long envId, string project, StackUpdatePlan? plan)
        => plan is null || _cache.Report(envId, project) is not { } look
            ? []
            : look.Report.Images
                .Where(i => i.NewerTag is not null && plan.Items.All(p => p.Service != i.Service))
                .Select(i => (i.Service, i.NewerTag!))
                .ToList();

    /// <summary>The environments shown (the sidebar choice, or all) whose host MatDock can look at.</summary>
    private async Task<List<DockerEnvironment>> VisibleHostsAsync()
    {
        var envs = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        var selected = EnvSelection.Current(HttpContext) is { } id && envs.Any(e => e.Id == id) ? id : 0;
        return envs
            .Where(e => HostUpdateService.Supports(e) && (selected == 0 || e.Id == selected))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Looks for updates in every stack (the discovered ones too) and on every host shown (in the background: one after the other).</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var stacks = (await VisibleStacksAsync()).Select(x => x.Stack.Id).ToList();
        var hosts = (await VisibleHostsAsync()).Select(e => e.Id).ToList();
        var projectHosts = (await ProjectEnvironmentsAsync()).Select(e => e.Id).ToList();
        if (stacks.Count == 0 && hosts.Count == 0 && projectHosts.Count == 0)
        {
            return RedirectToPage(new { Q, SortKey });
        }

        var operation = _operations.Start("updates-check", "all", "updates", async ctx =>
        {
            var ct = ctx.CancellationToken;
            var automation = ctx.Services.GetRequiredService<UpdateAutomation>();
            var updates = ctx.Services.GetRequiredService<UpdateService>();
            var environments = ctx.Services.GetRequiredService<EnvironmentService>();
            var all = ctx.Services.GetRequiredService<StackService>();
            var batch = new UpdateCheckBatch(); // every host is asked once for all its stacks, the managed and the discovered ones
            var list = new List<Stack>();
            foreach (var id in stacks)
            {
                if (await all.GetAsync(id, ct) is { } stack)
                {
                    list.Add(stack);
                    ctx.Operation.Steps.Add(stack.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), stack.Name);
                }
            }

            // The projects MatDock did not deploy, as they run on the hosts right now.
            var projects = new List<(DockerEnvironment Env, string Project)>();
            foreach (var id in projectHosts)
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

            var envs = await HostEnvironmentsAsync(ctx, hosts);
            var pending = list.Count == 0 ? 0 : await automation.CheckStacksAsync(list, ctx.Log, ct, ctx.Operation.Steps, batch);
            pending += projects.Count == 0 ? 0 : await automation.CheckProjectsAsync(projects, ctx.Log, ct, ctx.Operation.Steps, batch);
            ctx.Log(pending == 0 ? "No stack to update." : $"{pending} stack(s) can be updated.");
            await CheckHostsAsync(ctx, envs);
            return OperationOutcome.Success();
        });
        return RedirectToPage(new { op = operation.Id, Q, SortKey });
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
            await CheckHostsAsync(ctx, await HostEnvironmentsAsync(ctx, [env.Id]));
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

    private static async Task<List<DockerEnvironment>> HostEnvironmentsAsync(OperationContext ctx, IEnumerable<long> ids)
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

    private static async Task CheckHostsAsync(OperationContext ctx, IReadOnlyList<DockerEnvironment> envs)
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

    /// <summary>Updates the stacks shown that have something to update, one after the other, each with the backup and the health gate of its rule.</summary>
    public async Task<IActionResult> OnPostUpdateAllAsync()
    {
        var stacks = await VisibleStacksAsync();
        var ids = stacks
            .Where(x => _cache.Plan(x.Env.Id, x.Stack.Name) is { Plan.Items.Count: > 0 })
            .Select(x => x.Stack.Id)
            .ToList();
        var projects = (await VisibleProjectsAsync(stacks))
            .Where(p => _cache.Plan(p.Env.Id, p.Project) is { Plan.Items.Count: > 0 } && _cache.Blocked(p.Env.Id, p.Project) is null)
            .Select(p => (p.Env.Id, p.Project))
            .ToList();
        return ids.Count == 0 && projects.Count == 0 ? RedirectToPage(new { Q, SortKey }) : StartUpdate(ids, projects, "all");
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
    {
        var operation = _operations.Start("updates", target, "updates", async ctx =>
        {
            var ct = ctx.CancellationToken;
            var automation = ctx.Services.GetRequiredService<UpdateAutomation>();
            var environments = ctx.Services.GetRequiredService<EnvironmentService>();
            var all = ctx.Services.GetRequiredService<StackService>();
            var list = new List<Stack>();
            foreach (var id in ids)
            {
                if (await all.GetAsync(id, ct) is { } stack)
                {
                    list.Add(stack);
                    ctx.Operation.Steps.Add(stack.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), stack.Name);
                }
            }

            var discovered = new List<(DockerEnvironment Env, string Project)>();
            foreach (var (envId, project) in projects)
            {
                if (await environments.GetAsync(envId, ct) is { IsEnabled: true } env)
                {
                    discovered.Add((env, project));
                    ctx.Operation.Steps.Add(UpdateAutomation.ProjectStepKey(env.Id, project), project);
                }
            }

            var (updated, failed) = list.Count == 0 ? (0, 0) : await automation.UpdateStacksAsync(list, ctx.Log, ct, ctx.Operation.Steps);
            if (discovered.Count > 0)
            {
                var (projectsUpdated, projectsFailed) = await automation.UpdateProjectsAsync(discovered, ctx.Log, ct, ctx.Operation.Steps);
                updated += projectsUpdated;
                failed += projectsFailed;
            }

            ctx.Log($"{updated} updated, {failed} failed.");
            return failed == 0 ? OperationOutcome.Success() : OperationOutcome.Failure($"{failed} stack(s) failed.");
        });
        return RedirectToPage(new { op = operation.Id, Q, SortKey });
    }
}
