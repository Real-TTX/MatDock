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

    public IndexModel(EnvironmentService environments, StackService stacks, UpdatePolicyService policies, UpdateCheckCache cache, OperationRegistry operations)
    {
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
    }

    /// <summary>Looks for updates in every stack shown (in the background: the registries are asked one stack after the other).</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var stacks = (await VisibleStacksAsync()).Select(x => x.Stack.Id).ToList();
        if (stacks.Count == 0)
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
                }
            }

            var pending = await automation.CheckStacksAsync(list, ctx.Log, ctx.CancellationToken);
            ctx.Log(pending == 0 ? "Nothing to update." : $"{pending} stack(s) can be updated.");
            return OperationOutcome.Success();
        });
        return RedirectToPage(new { op = operation.Id });
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
                }
            }

            var (updated, failed) = await automation.UpdateStacksAsync(list, ctx.Log, ctx.CancellationToken);
            ctx.Log($"{updated} updated, {failed} failed.");
            return failed == 0 ? OperationOutcome.Success() : OperationOutcome.Failure($"{failed} stack(s) failed.");
        });
        return RedirectToPage(new { op = operation.Id });
    }
}
