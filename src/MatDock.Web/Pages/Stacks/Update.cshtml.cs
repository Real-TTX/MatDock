using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class UpdateModel : PageModel
{
    private readonly UpdateService _updates;
    private readonly StackService _stacks;
    private readonly EnvironmentService _environments;
    private readonly MatDock.Core.Operations.OperationRegistry _operations;
    private readonly UpdatePolicyService _policies;
    private readonly UpdateCheckCache _cache;

    /// <summary>How old the last look may be to be shown as it is; an older one is taken again when the page opens ("Re-check" always takes a new one).</summary>
    public static readonly TimeSpan LookFor = TimeSpan.FromHours(6);

    public UpdateModel(UpdateService updates, StackService stacks, EnvironmentService environments, MatDock.Core.Operations.OperationRegistry operations,
        UpdatePolicyService policies, UpdateCheckCache cache)
    {
        _operations = operations;
        _policies = policies;
        _cache = cache;
        _updates = updates;
        _stacks = stacks;
        _environments = environments;
    }

    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public bool PreBackup { get; set; } = true;

    /// <summary>Wait for the containers to be up and healthy, and put the old images back when they are not.</summary>
    [BindProperty] public bool HealthGate { get; set; } = true;

    /// <summary>The update that runs or just ran (?op=): its steps and their progress on the page.</summary>
    [BindProperty(SupportsGet = true)] public string? Op { get; set; }

    public MatDock.Core.Operations.Operation? Running { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public Stack? Stack { get; private set; }
    public string? EnvName { get; private set; }
    public StackUpdateReport? Report { get; private set; }

    /// <summary>What the rule of the stack makes of the look: the services an update would change, and to what (the same the overview shows).</summary>
    public StackUpdatePlan? Plan { get; private set; }

    /// <summary>When the look on the page was taken.</summary>
    public DateTime? CheckedUtc { get; private set; }

    public EffectiveUpdatePolicy Policy { get; private set; } = EffectiveUpdatePolicy.Default;
    public IReadOnlyList<StackUpdateRun> History { get; private set; } = Array.Empty<StackUpdateRun>();

    /// <summary>The image each service has in the compose file, and the ones MatDock runs instead.</summary>
    public IReadOnlyDictionary<string, string> ComposeImages { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, ImageOverride> Overrides { get; private set; } = new Dictionary<string, ImageOverride>();

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Running = string.IsNullOrEmpty(Op) ? null : _operations.Get(Op);
        Stack = await _stacks.GetAsync(Id, ct);
        if (Stack is null)
        {
            return RedirectToPage("Index");
        }

        var env = await _environments.GetAsync(Stack.EnvironmentId, ct);
        if (env is null)
        {
            Report = StackUpdateReport.Failed("Environment not available.");
            return Page();
        }

        EnvName = env.Name;
        // The page reads itself again every 2 s while an update runs (matdock.js, only the run's section is taken): no registry check for that.
        if (Running is not null && Request.Headers["X-Requested-With"] == "matdock-live")
        {
            return Page();
        }

        ComposeImages = ImageOverrides.ComposeImages(Stack.ComposeYaml);
        Overrides = ImageOverrides.Of(Stack);
        Policy = (await _policies.LoadAsync(ct)).For(Stack);
        if (HttpMethods.IsGet(Request.Method))
        {
            // What the rule says, as the start of the two boxes; the user may still choose otherwise for this run.
            PreBackup = Policy.PreBackup;
            HealthGate = Policy.Rollback;
        }

        // The last look (of "Check all", the automation or this page) is shown at once: asking the host and the registries takes seconds, and a
        // tab that is only opened should not cost them each time. "Re-check" takes a new one.
        if (_cache.Report(env.Id, Stack.Name) is { Report.NewerTags: true } last && DateTime.UtcNow - last.CheckedUtc < LookFor)
        {
            (Report, CheckedUtc) = last;
            Plan = _cache.Plan(env.Id, Stack.Name)?.Plan ?? await _updates.PlanAsync(env, Stack, Policy, Report, null, ct);
        }
        else
        {
            await LookAsync(env, ct);
        }

        History = await _updates.GetHistoryAsync(Stack.Id, 10, ct);
        return Page();
    }

    /// <summary>Asks the host and the registries now: the report of the images and what the rule makes of it.</summary>
    private async Task LookAsync(DockerEnvironment env, CancellationToken ct)
    {
        var batch = UpdateCheckBatch.ForOneStack();
        Report = await _updates.CheckStackAsync(env, Stack!.Name, ct, newerTags: true, batch);
        Plan = await _updates.PlanAsync(env, Stack, Policy, Report, batch, ct);
        CheckedUtc = DateTime.UtcNow;
    }

    /// <summary>Looks for updates again now ("Re-check").</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var ct = HttpContext.RequestAborted;
        Stack = await _stacks.GetAsync(Id, ct);
        if (Stack is null || await _environments.GetAsync(Stack.EnvironmentId, ct) is not { } env)
        {
            return RedirectToPage("Index");
        }

        Policy = (await _policies.LoadAsync(ct)).For(Stack);
        await LookAsync(env, ct);
        if (Report?.Error is null)
        {
            var count = Plan?.Items.Count ?? 0;
            StatusMessage = count == 0 ? "Up to date." : $"{count} update(s) available.";
            IsError = false;
        }

        return RedirectToPage(new { Id });
    }

    /// <summary>A pin or another image changes what an update would do: the last look no longer says it.</summary>
    private async Task ForgetLookAsync()
    {
        if (await _stacks.GetAsync(Id, HttpContext.RequestAborted) is { } stack)
        {
            _cache.Clear(stack.EnvironmentId, stack.Name);
        }
    }

    /// <summary>The address a CI calls to redeploy this stack, or null when the webhook is off.</summary>
    public string? WebhookUrl => Stack?.WebhookToken is { } token ? $"{Request.Scheme}://{Request.Host}/webhooks/stacks/{token}" : null;

    /// <summary>Switches the redeploy webhook on with a new secret, or off.</summary>
    public async Task<IActionResult> OnPostWebhookAsync(bool enabled)
    {
        var (ok, _) = await _stacks.SetWebhookAsync(Id, enabled, HttpContext.RequestAborted);
        StatusMessage = !ok ? "Stack not found." : enabled ? "Webhook on: a former address no longer works." : "Webhook off.";
        IsError = !ok;
        return RedirectToPage(new { Id });
    }

    /// <summary>Pins or unpins one service: updates skip a pinned service.</summary>
    public async Task<IActionResult> OnPostPinAsync(string service, bool pin)
    {
        var (ok, message) = await _stacks.SetPinnedAsync(Id, service, pin, HttpContext.RequestAborted);
        await ForgetLookAsync();
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { Id });
    }

    /// <summary>Makes MatDock run another image for a service than the compose file names (empty = the compose file's again). Applied by the next update.</summary>
    public async Task<IActionResult> OnPostImageAsync(string service, string? image, bool reset)
    {
        var (ok, message) = await _stacks.SetImageOverrideAsync(Id, service, reset ? null : image, HttpContext.RequestAborted);
        await ForgetLookAsync();
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { Id });
    }

    /// <summary>
    /// Updates the stack in the background, as steps the page follows: the backup (when asked), the new images (with their download) and the redeploy, and
    /// the health check (when asked). The log lines of the update say which step it is in.
    /// </summary>
    public async Task<IActionResult> OnPostUpdateAsync()
    {
        var stack = await _stacks.GetAsync(Id, HttpContext.RequestAborted);
        if (stack is null)
        {
            return RedirectToPage("Index");
        }

        var (id, pre, safety) = (Id, PreBackup, HealthGate ? UpdateSafety.Default : null);
        var operation = _operations.Start("update", stack.Name, MatDock.Web.Api.StackOperations.LockKey(stack.EnvironmentId, stack.Name), async ctx =>
        {
            var steps = ctx.Operation.Steps;
            steps.Add("plan", "New images");
            if (pre)
            {
                steps.Add("backup", "Backup of the volumes");
            }

            steps.Add("update", "New images and redeploy");
            if (safety is not null)
            {
                steps.Add("health", "Health check");
            }

            string? current = null;
            void Next(string key, string detail)
            {
                if (current is not null)
                {
                    steps.Set(current, MatDock.Core.Operations.StepState.Done);
                }

                current = key;
                steps.Set(key, MatDock.Core.Operations.StepState.Running, detail);
            }

            void Log(string line)
            {
                ctx.Log(line);
                if (line.StartsWith("==> Bundle backup", StringComparison.Ordinal)) { Next("backup", "Backing up"); }
                else if (line.StartsWith("==> docker compose", StringComparison.Ordinal)) { Next("update", "Pulling the new images"); }
                else if (line.StartsWith("==> Waiting", StringComparison.Ordinal)) { Next("health", "Waiting for the containers to be healthy"); }
                else if (line.StartsWith("==> Rolling back", StringComparison.Ordinal) && current is not null) { steps.Set(current, MatDock.Core.Operations.StepState.Failed, "Not healthy: rolling back to the old images"); current = null; }
            }

            // The same update the overview runs: what the rule of the stack makes of a new look (a moved tag is pulled, a newer version is set as the
            // image MatDock runs), with the backup and the health check chosen on the page.
            var ct = ctx.CancellationToken;
            var updates = ctx.Services.GetRequiredService<UpdateService>();
            var fresh = await ctx.Services.GetRequiredService<StackService>().GetAsync(id, ct);
            var env = fresh is null ? null : await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(fresh.EnvironmentId, ct);
            StackUpdateOutcome outcome;
            if (fresh is null || env is null)
            {
                steps.Set("plan", MatDock.Core.Operations.StepState.Skipped);
                outcome = await updates.RunUpdateAsync(id, pre, safety, ct, Log); // says what is missing
            }
            else
            {
                steps.Set("plan", MatDock.Core.Operations.StepState.Running, "Asking the registry");
                var policy = (await ctx.Services.GetRequiredService<UpdatePolicyService>().LoadAsync(ct)).For(fresh) with { PreBackup = pre, Rollback = safety is not null };
                var plan = await updates.PlanStackAsync(env, fresh, policy, ct);
                foreach (var item in plan.Items)
                {
                    ctx.Log($"{item.Service}: {item.Current} → {item.Target}");
                }

                steps.Set("plan", plan.Error is null ? MatDock.Core.Operations.StepState.Done : MatDock.Core.Operations.StepState.Skipped,
                    plan.Error ?? (plan.Items.Count == 0
                        ? "Nothing new known, pulling anyway"
                        : string.Join(", ", plan.Items.Select(i => i.Kind == PlannedKind.Version ? $"{i.Service} → {i.Target}" : $"{i.Service}: new image"))));
                outcome = await ctx.Services.GetRequiredService<UpdateAutomation>().ApplyAsync(fresh, plan.Items, policy, ct, "manual", Log);
            }

            var last = outcome.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (current is not null)
            {
                steps.Set(current, outcome.Ok ? MatDock.Core.Operations.StepState.Done : MatDock.Core.Operations.StepState.Failed, outcome.Ok ? null : last);
            }

            foreach (var waiting in steps.Snapshot().Where(s => s.State == MatDock.Core.Operations.StepState.Waiting))
            {
                steps.Set(waiting.Key, outcome.Ok ? MatDock.Core.Operations.StepState.Skipped : MatDock.Core.Operations.StepState.Skipped, outcome.Ok ? "Not needed" : "Not reached");
            }

            ctx.Log(outcome.Message);
            return outcome.Ok ? MatDock.Core.Operations.OperationOutcome.Success() : MatDock.Core.Operations.OperationOutcome.Failure(last ?? "The update failed.");
        });
        return RedirectToPage(new { Id, op = operation.Id });
    }
}
