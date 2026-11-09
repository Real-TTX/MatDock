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
        ("Environment", r => r.EnvName), ("Stack", r => r.Stack.Name), ("Updates", r => r.Pending), ("Checked", r => r.CheckedUtc));

    private readonly EnvironmentService _environments;
    private readonly StackService _stacks;
    private readonly UpdatePolicyService _policies;
    private readonly UpdateCheckCache _cache;
    private readonly OperationRegistry _operations;
    private readonly HostUpdateCache _hostCache;

    public IndexModel(EnvironmentService environments, StackService stacks, UpdatePolicyService policies, UpdateCheckCache cache, OperationRegistry operations,
        HostUpdateCache hostCache)
    {
        _hostCache = hostCache;
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

    public sealed record UpdateRow(Stack Stack, string EnvName, EffectiveUpdatePolicy Policy, StackUpdatePlan? Plan, DateTime? CheckedUtc)
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

    private async Task LoadAsync()
    {
        var policies = await _policies.LoadAsync(HttpContext.RequestAborted);
        var all = (await VisibleStacksAsync())
            .Select(x =>
            {
                var cached = _cache.Plan(x.Env.Id, x.Stack.Name);
                return new UpdateRow(x.Stack, x.Env.Name, policies.For(x.Stack), cached?.Plan, cached?.CheckedUtc);
            })
            .ToList();
        Total = all.Count;
        PendingStacks = all.Count(r => r.Pending > 0);
        Rows = ListView.Apply(all, Q, SortChoices, SortKey, r => r.Stack.Name, r => r.EnvName, r => r.Policy.Mode.ToString(),
            r => string.Join(" ", r.Plan?.Items.Select(i => i.Service) ?? []));
        Toolbar = ListView.Toolbar(Q, SortKey, "Search stacks", SortChoices);

        HostRows = (await VisibleHostsAsync())
            .Select(env => _hostCache.Get(env.Id) is { } cached ? new HostRow(env, cached.Report, cached.CheckedUtc) : new HostRow(env, null, null))
            .ToList();
    }

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

    /// <summary>Looks for updates in every stack and on every host shown (in the background: one after the other).</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var stacks = (await VisibleStacksAsync()).Select(x => x.Stack.Id).ToList();
        var hosts = (await VisibleHostsAsync()).Select(e => e.Id).ToList();
        if (stacks.Count == 0 && hosts.Count == 0)
        {
            return RedirectToPage();
        }

        var operation = _operations.Start("updates-check", "all", "updates", async ctx =>
        {
            var automation = ctx.Services.GetRequiredService<UpdateAutomation>();
            var all = ctx.Services.GetRequiredService<StackService>();
            var list = new List<Stack>();
            foreach (var id in stacks)
            {
                if (await all.GetAsync(id, ctx.CancellationToken) is { } stack)
                {
                    list.Add(stack);
                    ctx.Operation.Steps.Add(stack.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), stack.Name);
                }
            }

            var envs = await HostEnvironmentsAsync(ctx, hosts);
            var pending = list.Count == 0 ? 0 : await automation.CheckStacksAsync(list, ctx.Log, ctx.CancellationToken, ctx.Operation.Steps);
            ctx.Log(pending == 0 ? "No stack to update." : $"{pending} stack(s) can be updated.");
            await CheckHostsAsync(ctx, envs);
            return OperationOutcome.Success();
        });
        return RedirectToPage(new { op = operation.Id });
    }

    /// <summary>Looks at one host again (refreshing its package lists; in the background, it may take a minute).</summary>
    public async Task<IActionResult> OnPostCheckHostAsync(long id)
    {
        var env = (await VisibleHostsAsync()).FirstOrDefault(e => e.Id == id);
        if (env is null)
        {
            return RedirectToPage();
        }

        var operation = _operations.Start("updates-check", env.Name, $"host-update:{env.Id}", async ctx =>
        {
            await CheckHostsAsync(ctx, await HostEnvironmentsAsync(ctx, [env.Id]));
            return OperationOutcome.Success();
        });
        return RedirectToPage(new { op = operation.Id });
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
            return RedirectToPage();
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
        return RedirectToPage(new { op = operation.Id });
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
        var ids = (await VisibleStacksAsync())
            .Where(x => _cache.Plan(x.Env.Id, x.Stack.Name) is { Plan.Items.Count: > 0 })
            .Select(x => x.Stack.Id)
            .ToList();
        return ids.Count == 0 ? RedirectToPage() : StartUpdate(ids, "all");
    }

    /// <summary>Updates one stack the same way.</summary>
    public async Task<IActionResult> OnPostUpdateOneAsync(long id)
    {
        var stack = (await VisibleStacksAsync()).FirstOrDefault(x => x.Stack.Id == id).Stack;
        return stack is null ? RedirectToPage() : StartUpdate([stack.Id], stack.Name);
    }

    /// <summary>Looks for updates in one stack (quick enough to wait for).</summary>
    public async Task<IActionResult> OnPostCheckOneAsync(long id)
    {
        var found = (await VisibleStacksAsync()).FirstOrDefault(x => x.Stack.Id == id);
        if (found.Stack is null)
        {
            return RedirectToPage();
        }

        var lines = new List<string>();
        await HttpContext.RequestServices.GetRequiredService<UpdateAutomation>().CheckStacksAsync([found.Stack], lines.Add, HttpContext.RequestAborted);
        StatusMessage = string.Join(" ", lines);
        IsError = false;
        return RedirectToPage();
    }

    private IActionResult StartUpdate(List<long> ids, string target)
    {
        var operation = _operations.Start("updates", target, "updates", async ctx =>
        {
            var automation = ctx.Services.GetRequiredService<UpdateAutomation>();
            var all = ctx.Services.GetRequiredService<StackService>();
            var list = new List<Stack>();
            foreach (var id in ids)
            {
                if (await all.GetAsync(id, ctx.CancellationToken) is { } stack)
                {
                    list.Add(stack);
                    ctx.Operation.Steps.Add(stack.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), stack.Name);
                }
            }

            var (updated, failed) = await automation.UpdateStacksAsync(list, ctx.Log, ctx.CancellationToken, ctx.Operation.Steps);
            ctx.Log($"{updated} updated, {failed} failed.");
            return failed == 0 ? OperationOutcome.Success() : OperationOutcome.Failure($"{failed} stack(s) failed.");
        });
        return RedirectToPage(new { op = operation.Id });
    }
}
