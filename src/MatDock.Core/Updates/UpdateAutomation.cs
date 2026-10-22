using System.Collections.Concurrent;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Updates;

/// <summary>When each stack was last looked at by the automation (in memory: after a restart everything is looked at once).</summary>
public sealed class UpdateAutomationState
{
    private readonly ConcurrentDictionary<long, DateTime> _checked = new();

    public DateTime? LastCheck(long stackId) => _checked.TryGetValue(stackId, out var at) ? at : null;

    public void Checked(long stackId, DateTime utc) => _checked[stackId] = utc;

    public void Forget(long stackId) => _checked.TryRemove(stackId, out _);
}

/// <summary>What one run of the automation did.</summary>
public sealed record UpdateAutomationReport(int Looked, int Announced, int Applied, int Failed, IReadOnlyList<string> Lines);

/// <summary>
/// The update rules at work: for every managed stack whose rule says Notify or Auto, look for updates (as often as the rule says), announce a new version once,
/// and in Auto mode apply it within the time window, with the backup and the health gate of the rule. What was announced or failed is remembered
/// (<see cref="UpdateNotice"/>), so nothing is said twice and a failed update is not tried again until something newer is there.
/// </summary>
public sealed class UpdateAutomation
{
    /// <summary>Notices older than this are forgotten (a version that is still the newest is announced again after that).</summary>
    public static readonly TimeSpan NoticeAge = TimeSpan.FromDays(90);

    private readonly EnvironmentService _environments;
    private readonly StackService _stacks;
    private readonly UpdateService _updates;
    private readonly UpdatePolicyService _policies;
    private readonly INotificationService _notifications;
    private readonly UpdateAutomationState _state;
    private readonly MatDockDbContext _db;
    private readonly ILogger<UpdateAutomation> _logger;

    public UpdateAutomation(
        EnvironmentService environments,
        StackService stacks,
        UpdateService updates,
        UpdatePolicyService policies,
        INotificationService notifications,
        UpdateAutomationState state,
        MatDockDbContext db,
        ILogger<UpdateAutomation> logger)
    {
        _environments = environments;
        _stacks = stacks;
        _updates = updates;
        _policies = policies;
        _notifications = notifications;
        _state = state;
        _db = db;
        _logger = logger;
    }

    /// <summary>One pass over all stacks. <paramref name="utcNow"/> and <paramref name="localNow"/> (server time, for the window) are passed in so tests can choose them.</summary>
    public async Task<UpdateAutomationReport> RunAsync(DateTime utcNow, DateTime localNow, CancellationToken ct = default)
    {
        var lines = new List<string>();
        int looked = 0, announced = 0, applied = 0, failed = 0;

        var policies = await _policies.LoadAsync(ct);
        var envs = (await _environments.GetEnabledAsync(ct)).Where(e => e.Status != EnvironmentStatus.Offline).ToDictionary(e => e.Id);
        var stacks = (await _stacks.GetAllAsync(ct)).Where(s => envs.ContainsKey(s.EnvironmentId)).OrderBy(s => s.EnvironmentId).ThenBy(s => s.Name, StringComparer.Ordinal);

        foreach (var listed in stacks)
        {
            ct.ThrowIfCancellationRequested();
            var policy = policies.For(listed);
            if (policy.Mode == UpdateMode.Off)
            {
                continue;
            }

            if (_state.LastCheck(listed.Id) is { } last && utcNow - last < TimeSpan.FromHours(policy.CheckEveryHours))
            {
                continue;
            }

            try
            {
                var result = await HandleAsync(envs[listed.EnvironmentId], listed.Id, policy, utcNow, localNow, ct);
                looked++;
                announced += result.Announced;
                applied += result.Applied;
                failed += result.Failed;
                lines.AddRange(result.Lines);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Update automation for stack {Stack} failed.", listed.Name);
                lines.Add($"{listed.Name}: {ex.Message}");
            }
        }

        await ForgetOldNoticesAsync(utcNow, ct);
        return new UpdateAutomationReport(looked, announced, applied, failed, lines);
    }

    private sealed record StackResult(int Announced, int Applied, int Failed, IReadOnlyList<string> Lines);

    private async Task<StackResult> HandleAsync(DockerEnvironment env, long stackId, EffectiveUpdatePolicy policy, DateTime utcNow, DateTime localNow, CancellationToken ct)
    {
        // A fresh read: the stack row of the list is not the one that is saved.
        var stack = await _stacks.GetAsync(stackId, ct);
        if (stack is null)
        {
            return new StackResult(0, 0, 0, []);
        }

        var plan = await _updates.PlanStackAsync(env, stack, policy, ct);
        if (plan.Error is not null)
        {
            // Not looked at: try again at the next pass rather than wait out the interval.
            return new StackResult(0, 0, 0, [$"{stack.Name}: {plan.Error}"]);
        }

        _state.Checked(stack.Id, utcNow);
        if (plan.Items.Count == 0)
        {
            return new StackResult(0, 0, 0, []);
        }

        var notices = await _db.UpdateNotices.Where(n => n.StackId == stack.Id).ToListAsync(ct);
        UpdateNotice? Notice(PlannedUpdate item) => notices.FirstOrDefault(n => n.Service == item.Service && n.Target == item.Target);

        var applicable = plan.Items.Where(i => Notice(i) is not { Failed: true }).ToList();
        var willApply = policy.Mode == UpdateMode.Auto && policy.InWindow(localNow) && applicable.Count > 0;
        var lines = new List<string>();
        int announced = 0, applied = 0, failed = 0;

        // What is new and not said yet is said now, unless MatDock applies it right away (then the result is what there is to say).
        var fresh = plan.Items.Where(i => Notice(i) is null).ToList();
        if (!willApply && fresh.Count > 0 && policy.NotifyAvailable)
        {
            await NotifyAsync($"Update available: {stack.Name} on {env.Name}", Describe(plan.Items, "New images"), ct);
            announced = fresh.Count;
            lines.Add($"{stack.Name}: {fresh.Count} new image(s) announced.");
        }

        if (!willApply)
        {
            // Recorded (announced or not), so the next pass does not announce them again.
            foreach (var item in fresh)
            {
                AddNotice(stack.Id, item, failed: false, utcNow, notified: policy.NotifyAvailable);
            }

            await _db.SaveChangesAsync(ct);
            return new StackResult(announced, 0, 0, lines);
        }

        var outcome = await ApplyAsync(stack, applicable, policy, ct);
        foreach (var item in applicable)
        {
            if (Notice(item) is { } row)
            {
                row.Failed = !outcome.Ok;
                row.NotifiedUtc ??= utcNow;
            }
            else
            {
                AddNotice(stack.Id, item, failed: !outcome.Ok, utcNow, notified: true);
            }
        }

        await _db.SaveChangesAsync(ct);
        _state.Forget(stack.Id); // looked at again after the update

        if (outcome.Ok)
        {
            applied = applicable.Count;
            lines.Add($"{stack.Name}: updated {applicable.Count} service(s).");
            if (policy.NotifyResult)
            {
                await NotifyAsync($"Updated: {stack.Name} on {env.Name}", Describe(applicable, "Updated") + (outcome.Changed ? string.Empty : "\nNo container needed to change."), ct);
            }
        }
        else
        {
            failed = applicable.Count;
            lines.Add($"{stack.Name}: update failed{(outcome.RolledBack ? " and was rolled back" : string.Empty)}.");
            if (policy.NotifyResult)
            {
                await NotifyAsync($"Update failed: {stack.Name} on {env.Name}",
                    Describe(applicable, "Tried") + $"\n{(outcome.RolledBack ? "The old images were put back.\n" : string.Empty)}{LastLines(outcome.Message)}", ct);
            }
        }

        return new StackResult(announced, applied, failed, lines);
    }

    /// <summary>Applies the planned updates: the versions by an image override, then the update itself (pull and redeploy, with the backup and the gate of the rule).
    /// An update that did not happen (the backup or the pull failed) or was rolled back also takes the overrides back; one that brought up unhealthy containers without a rollback keeps them (the stack runs what they say).</summary>
    private async Task<StackUpdateOutcome> ApplyAsync(Stack stack, IReadOnlyList<PlannedUpdate> items, EffectiveUpdatePolicy policy, CancellationToken ct)
    {
        var before = stack.ImageOverridesJson;
        foreach (var item in items.Where(i => i.Kind == PlannedKind.Version))
        {
            var (ok, message) = await _stacks.SetImageOverrideAsync(stack.Id, item.Service, item.Target, ct);
            if (!ok)
            {
                await _stacks.RestoreImageOverridesAsync(stack.Id, before, ct);
                return StackUpdateOutcome.NotStartedWith($"{item.Service}: {message}");
            }
        }

        var outcome = await _updates.RunUpdateAsync(stack.Id, policy.PreBackup, policy.Rollback ? UpdateSafety.Default : null, ct, trigger: "auto");
        if (!outcome.Ok && (outcome.RolledBack || !outcome.Deployed))
        {
            await _stacks.RestoreImageOverridesAsync(stack.Id, before, ct);
        }

        return outcome;
    }

    private void AddNotice(long stackId, PlannedUpdate item, bool failed, DateTime utcNow, bool notified)
        => _db.UpdateNotices.Add(new UpdateNotice { StackId = stackId, Service = item.Service, Target = Truncate(item.Target, 200), Failed = failed, NotifiedUtc = notified ? utcNow : null });

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private async Task NotifyAsync(string subject, string body, CancellationToken ct)
    {
        try
        {
            await _notifications.NotifyAsync(subject, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Sending the update notification failed.");
        }
    }

    private static string Describe(IEnumerable<PlannedUpdate> items, string heading)
        => heading + ":\n" + string.Join("\n", items.Select(i => i.Kind == PlannedKind.Version
            ? $"- {i.Service}: {i.Current} → {i.Target}"
            : $"- {i.Service}: {i.Current} (new digest {i.Target})"));

    private static string LastLines(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines.TakeLast(3));
    }

    private async Task ForgetOldNoticesAsync(DateTime utcNow, CancellationToken ct)
    {
        var limit = utcNow - NoticeAge;
        var old = await _db.UpdateNotices.Where(n => n.CreateDate < limit).ToListAsync(ct);
        if (old.Count > 0)
        {
            _db.UpdateNotices.RemoveRange(old);
            await _db.SaveChangesAsync(ct);
        }
    }
}
