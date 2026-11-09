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

    // Discovered projects (no row of their own): by "environment/project", and what was seen of their images by "environment/project/service/digest".
    private readonly ConcurrentDictionary<string, DateTime> _projectChecked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectNotice> _projectNotices = new(StringComparer.Ordinal);

    public DateTime? LastCheck(string project) => _projectChecked.TryGetValue(project, out var at) ? at : null;

    public void Checked(string project, DateTime utc) => _projectChecked[project] = utc;

    public void Forget(string project) => _projectChecked.TryRemove(project, out _);

    /// <summary>What is known of a new image of a discovered project (first seen, said, failed); null = not seen yet.</summary>
    public ProjectNotice? Notice(string key) => _projectNotices.TryGetValue(key, out var n) ? n : null;

    public void Note(string key, ProjectNotice notice) => _projectNotices[key] = notice;
}

/// <summary>A new image of a discovered project as the automation saw it: since when, whether it was said, whether applying it failed.</summary>
public sealed record ProjectNotice(DateTime FirstSeenUtc, bool Notified, bool Failed);

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
    private readonly Images.ImageScanService? _scans;

    public UpdateAutomation(
        EnvironmentService environments,
        StackService stacks,
        UpdateService updates,
        UpdatePolicyService policies,
        INotificationService notifications,
        UpdateAutomationState state,
        MatDockDbContext db,
        ILogger<UpdateAutomation> logger,
        Images.ImageScanService? scans = null)
    {
        _scans = scans;
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

        // The compose projects MatDock did not deploy, under the rule of their environment: moved tags, updated in place with their own files.
        foreach (var env in envs.Values.Where(e => e.ConnectionType != ConnectionType.Bridge).OrderBy(e => e.Id))
        {
            ct.ThrowIfCancellationRequested();
            var policy = policies.Inherited(UpdatePolicyScope.Stack, env.Id);
            if (policy.Mode == UpdateMode.Off)
            {
                continue;
            }

            IReadOnlyList<string> projects;
            try
            {
                projects = await _updates.DiscoveredProjectsAsync(env, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lines.Add($"{env.Name}: {ex.Message}");
                continue;
            }

            var unapplied = new List<string>();
            foreach (var project in projects)
            {
                var key = $"{env.Id}/{project}";
                if (_state.LastCheck(key) is { } last && utcNow - last < TimeSpan.FromHours(policy.CheckEveryHours))
                {
                    continue;
                }

                try
                {
                    var result = await HandleProjectAsync(env, project, key, policy, utcNow, localNow, ct);
                    looked++;
                    announced += result.Announced;
                    applied += result.Applied;
                    failed += result.Failed;
                    lines.AddRange(result.Lines);
                    if (result.Unapplied is { } line)
                    {
                        unapplied.Add(line);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Update automation for project {Project} on {Environment} failed.", project, env.Name);
                    lines.Add($"{project} on {env.Name}: {ex.Message}");
                }
            }

            if (unapplied.Count > 0)
            {
                await NotifyAsync($"Updates available that MatDock does not apply: {env.Name}",
                    "New images for stacks MatDock did not deploy and cannot update in place (update them where they are managed):\n"
                    + string.Join("\n", unapplied.Select(u => "- " + u)), ct);
            }
        }

        await ForgetOldNoticesAsync(utcNow, ct);
        return new UpdateAutomationReport(looked, announced, applied, failed, lines);
    }

    /// <param name="Unapplied">A discovered project with new images MatDock cannot apply by itself, as one line of the message for its environment.</param>
    private sealed record StackResult(int Announced, int Applied, int Failed, IReadOnlyList<string> Lines, string? Unapplied = null);

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
        var lines = new List<string>();

        // A new image waits its minimum age (since MatDock first saw it) before MatDock applies it by itself.
        var waiting = new List<PlannedUpdate>();
        if (policy.Mode == UpdateMode.Auto && policy.MinAgeHours > 0)
        {
            var minAge = TimeSpan.FromHours(policy.MinAgeHours);
            waiting = applicable.Where(i => Notice(i) is not { } n || utcNow - (n.FirstSeenUtc ?? n.CreateDate) < minAge).ToList();
            applicable = applicable.Except(waiting).ToList();
            if (waiting.Count > 0)
            {
                lines.Add($"{stack.Name}: {waiting.Count} new image(s) wait for their minimum age of {policy.MinAgeHours} h.");
            }
        }

        var willApply = policy.Mode == UpdateMode.Auto && policy.InWindow(localNow) && applicable.Count > 0;
        if (willApply && policy.VulnerabilityGate != VulnerabilityGate.Off && _scans is not null)
        {
            // The new images are scanned before they are pulled; one that fails the check is held back (and not tried again until a newer one comes).
            var held = await GateAsync(env, applicable, policy.VulnerabilityGate, ct);
            foreach (var (item, reason) in held)
            {
                applicable.Remove(item);
                if (Notice(item) is { } row)
                {
                    row.Failed = true;
                    row.NotifiedUtc ??= utcNow;
                }
                else
                {
                    notices.Add(AddNotice(stack.Id, item, failed: true, utcNow, notified: true));
                }

                lines.Add($"{stack.Name}: {item.Service} held back: {reason}");
            }

            if (held.Count > 0 && policy.NotifyResult)
            {
                await NotifyAsync($"Update held back: {stack.Name} on {env.Name}",
                    string.Join("\n", held.Select(h => $"{h.Item.Service} → {h.Item.Target}: {h.Reason}")), ct);
            }

            willApply = applicable.Count > 0;
            if (!willApply)
            {
                await _db.SaveChangesAsync(ct);
            }
        }

        // The images that wait start their clock now (recorded once; when nothing is applied the branch below records and announces them).
        if (willApply)
        {
            foreach (var item in waiting.Where(i => Notice(i) is null))
            {
                notices.Add(AddNotice(stack.Id, item, failed: false, utcNow, notified: false));
            }
        }
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

    /// <summary>
    /// A discovered project: its images with a moved tag (a project MatDock did not deploy has no image overrides, so a newer version tag stays a hint on the
    /// update page). Announced once; in Auto within the window updated in place (<see cref="UpdateService.RunProjectUpdateAsync"/>). One MatDock cannot
    /// update by itself (its compose files are not on the host) is said once as available, with the reason, and not tried again for the same image.
    /// </summary>
    private async Task<StackResult> HandleProjectAsync(DockerEnvironment env, string project, string key, EffectiveUpdatePolicy policy,
        DateTime utcNow, DateTime localNow, CancellationToken ct)
    {
        var report = await _updates.CheckStackAsync(env, project, ct);
        if (report.Error is not null)
        {
            return new StackResult(0, 0, 0, [$"{project} on {env.Name}: {report.Error}"]);
        }

        _state.Checked(key, utcNow);
        var items = report.Images
            .Where(i => i.UpdateAvailable && !i.Pinned && policy.StrategyOf(i.Service) != UpdateStrategy.Pinned)
            .Select(i => (Status: i, Key: $"{key}/{i.Service}/{i.RegistryDigest}"))
            .ToList();
        if (items.Count == 0)
        {
            return new StackResult(0, 0, 0, []);
        }

        foreach (var (_, noticeKey) in items.Where(i => _state.Notice(i.Key) is null))
        {
            _state.Note(noticeKey, new ProjectNotice(utcNow, Notified: false, Failed: false));
        }

        ProjectNotice Notice(string noticeKey) => _state.Notice(noticeKey)!;
        PlannedUpdate Planned(ImageUpdateStatus s) => new(s.Service, s.Image, UpdateService.ShortDigest(s.RegistryDigest), PlannedKind.Digest);
        var lines = new List<string>();
        var name = $"{project} on {env.Name}";

        var applicable = items.Where(i => !Notice(i.Key).Failed).ToList();
        if (policy.Mode == UpdateMode.Auto && policy.MinAgeHours > 0)
        {
            var minAge = TimeSpan.FromHours(policy.MinAgeHours);
            var waiting = applicable.Where(i => utcNow - Notice(i.Key).FirstSeenUtc < minAge).ToList();
            applicable = applicable.Except(waiting).ToList();
            if (waiting.Count > 0)
            {
                lines.Add($"{name}: {waiting.Count} new image(s) wait for their minimum age of {policy.MinAgeHours} h.");
            }
        }

        var unannounced = items.Where(i => !Notice(i.Key).Notified).ToList();
        void Said(IEnumerable<(ImageUpdateStatus Status, string Key)> which, bool? failed = null)
        {
            foreach (var (_, noticeKey) in which)
            {
                var n = Notice(noticeKey);
                _state.Note(noticeKey, n with { Notified = true, Failed = failed ?? n.Failed });
            }
        }

        var willApply = policy.Mode == UpdateMode.Auto && policy.InWindow(localNow) && applicable.Count > 0;
        if (!willApply)
        {
            var announced = 0;
            if (unannounced.Count > 0 && policy.NotifyAvailable)
            {
                await NotifyAsync($"Update available: {name}", Describe(items.Select(i => Planned(i.Status)), "New images"), ct);
                announced = unannounced.Count;
                lines.Add($"{name}: {unannounced.Count} new image(s) announced.");
            }

            Said(unannounced);
            return new StackResult(announced, 0, 0, lines);
        }

        var services = applicable.Select(i => i.Status.Service).Distinct(StringComparer.Ordinal).ToList();
        var outcome = await _updates.RunProjectUpdateAsync(env, project, services, policy.PreBackup, policy.Rollback ? UpdateSafety.Default : null, ct);
        if (outcome.Transient)
        {
            lines.Add($"{name}: {outcome.Message}");
            return new StackResult(0, 0, 0, lines);
        }

        if (!outcome.Applicable)
        {
            // Nothing was touched: said once as available (gathered per environment, one message for all), and not tried again for these images.
            string? unapplied = null;
            if (unannounced.Count > 0 && policy.NotifyAvailable)
            {
                unapplied = $"{project}: {string.Join(", ", items.Select(i => i.Status.Service).Distinct(StringComparer.Ordinal))} — {outcome.Message}";
            }

            Said(items, failed: true);
            lines.Add($"{name}: not updated by MatDock: {outcome.Message}");
            return new StackResult(unapplied is null ? 0 : unannounced.Count, 0, 0, lines, unapplied);
        }

        Said(applicable, failed: !outcome.Ok);
        _state.Forget(key); // looked at again after the update
        var tried = applicable.Select(i => Planned(i.Status)).ToList();
        if (outcome.Ok)
        {
            lines.Add($"{name}: updated {services.Count} service(s).");
            if (policy.NotifyResult)
            {
                await NotifyAsync($"Updated: {name}", Describe(tried, "Updated") + (outcome.Changed ? string.Empty : "\nNo container needed to change."), ct);
            }

            return new StackResult(0, applicable.Count, 0, lines);
        }

        lines.Add($"{name}: update failed{(outcome.RolledBack ? " and was rolled back" : string.Empty)}.");
        if (policy.NotifyResult)
        {
            await NotifyAsync($"Update failed: {name}",
                Describe(tried, "Tried") + $"\n{(outcome.RolledBack ? "The old images were put back.\n" : string.Empty)}{LastLines(outcome.Message)}", ct);
        }

        return new StackResult(0, 0, applicable.Count, lines);
    }

    /// <summary>Applies the planned updates: the versions by an image override, then the update itself (pull and redeploy, with the backup and the gate of the rule).
    /// An update that did not happen (the backup or the pull failed) or was rolled back also takes the overrides back; one that brought up unhealthy containers without a rollback keeps them (the stack runs what they say).</summary>
    public async Task<StackUpdateOutcome> ApplyAsync(Stack stack, IReadOnlyList<PlannedUpdate> items, EffectiveUpdatePolicy policy, CancellationToken ct, string trigger = "auto")
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

        var outcome = await _updates.RunUpdateAsync(stack.Id, policy.PreBackup, policy.Rollback ? UpdateSafety.Default : null, ct, trigger: trigger);
        if (!outcome.Ok && (outcome.RolledBack || !outcome.Deployed))
        {
            await _stacks.RestoreImageOverridesAsync(stack.Id, before, ct);
        }

        return outcome;
    }

    /// <summary>
    /// Looks for updates in the given stacks now (what their rules would update), whatever their mode, and keeps the result for the overview. Answers how many
    /// have some. With <paramref name="steps"/> every stack is an entry of it (key: its id) that runs and ends with its result.
    /// </summary>
    public async Task<int> CheckStacksAsync(IEnumerable<Stack> stacks, Action<string> log, CancellationToken ct = default, Operations.OperationSteps? steps = null)
    {
        var policies = await _policies.LoadAsync(ct);
        var pending = 0;
        foreach (var listed in stacks)
        {
            ct.ThrowIfCancellationRequested();
            var key = listed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            steps?.Set(key, Operations.StepState.Running, "Asking the registry");
            var env = await _environments.GetAsync(listed.EnvironmentId, ct);
            if (env is null || !env.IsEnabled)
            {
                log($"{listed.Name}: environment not available.");
                steps?.Set(key, Operations.StepState.Skipped, "Environment not available");
                continue;
            }

            var plan = await _updates.PlanStackAsync(env, listed, policies.For(listed), ct);
            if (plan.Error is not null)
            {
                log($"{listed.Name} on {env.Name}: {plan.Error}");
                steps?.Set(key, Operations.StepState.Failed, plan.Error);
            }
            else if (plan.Items.Count == 0)
            {
                log($"{listed.Name} on {env.Name}: up to date.");
                steps?.Set(key, Operations.StepState.Done, "Up to date");
            }
            else
            {
                pending++;
                log($"{listed.Name} on {env.Name}: {string.Join(", ", plan.Items.Select(i => i.Service))}");
                steps?.Set(key, Operations.StepState.Done, $"{plan.Items.Count} update(s): {string.Join(", ", plan.Items.Select(i => i.Service))}");
            }
        }

        return pending;
    }

    /// <summary>
    /// Updates the given stacks now, one after the other, whatever their mode: each is looked at again and gets what its rule makes of it, with the backup and the
    /// health gate of that rule. A stack with nothing to update is skipped. With <paramref name="steps"/> every stack is an entry of it (key: its id).
    /// </summary>
    public async Task<(int Updated, int Failed)> UpdateStacksAsync(IEnumerable<Stack> stacks, Action<string> log, CancellationToken ct = default, Operations.OperationSteps? steps = null)
    {
        var policies = await _policies.LoadAsync(ct);
        int updated = 0, failed = 0;
        foreach (var listed in stacks)
        {
            ct.ThrowIfCancellationRequested();
            var key = listed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            steps?.Set(key, Operations.StepState.Running, "Looking for the new images");
            var env = await _environments.GetAsync(listed.EnvironmentId, ct);
            var stack = await _stacks.GetAsync(listed.Id, ct);
            if (env is null || !env.IsEnabled || stack is null)
            {
                log($"{listed.Name}: environment not available.");
                steps?.Set(key, Operations.StepState.Skipped, "Environment not available");
                continue;
            }

            var policy = policies.For(stack);
            var plan = await _updates.PlanStackAsync(env, stack, policy, ct);
            if (plan.Error is not null)
            {
                failed++;
                log($"{stack.Name} on {env.Name}: {plan.Error}");
                steps?.Set(key, Operations.StepState.Failed, plan.Error);
                continue;
            }

            if (plan.Items.Count == 0)
            {
                log($"{stack.Name} on {env.Name}: up to date.");
                steps?.Set(key, Operations.StepState.Skipped, "Up to date");
                continue;
            }

            log($"==> {stack.Name} on {env.Name}: {string.Join(", ", plan.Items.Select(i => i.Service))}");
            steps?.Set(key, Operations.StepState.Running, $"Updating {string.Join(", ", plan.Items.Select(i => i.Service))}");
            var outcome = await ApplyAsync(stack, plan.Items, policy, ct, trigger: "manual");
            if (outcome.Ok)
            {
                updated++;
                log($"{stack.Name}: updated.");
                steps?.Set(key, Operations.StepState.Done, outcome.Changed ? $"Updated: {string.Join(", ", plan.Items.Select(i => i.Service))}" : "Nothing changed");
            }
            else
            {
                failed++;
                log($"{stack.Name}: failed{(outcome.RolledBack ? " and rolled back" : string.Empty)}: {LastLines(outcome.Message)}");
                steps?.Set(key, Operations.StepState.Failed, (outcome.RolledBack ? "Rolled back: " : string.Empty) + LastLines(outcome.Message));
            }
        }

        return (updated, failed);
    }

    /// <summary>
    /// The planned updates whose new image fails the vulnerability check, with the reason. The new image is scanned by its exact reference (the version, or the
    /// repository with the new digest) before anything is pulled; one that cannot be scanned is held back too (it cannot be shown to be fine).
    /// </summary>
    internal async Task<List<(PlannedUpdate Item, string Reason)>> GateAsync(DockerEnvironment env, IReadOnlyList<PlannedUpdate> items, VulnerabilityGate gate, CancellationToken ct)
    {
        var held = new List<(PlannedUpdate, string)>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ScanRef))
            {
                held.Add((item, "the new image cannot be named for a scan."));
                continue;
            }

            var (ok, message, summary) = await _scans!.ScanAsync(env, item.ScanRef, ct);
            if (!ok || summary is null)
            {
                held.Add((item, $"the scan failed ({message})."));
                continue;
            }

            var refused = gate switch
            {
                VulnerabilityGate.Any => summary.Total > 0,
                VulnerabilityGate.CriticalHigh => summary.Critical + summary.High > 0,
                VulnerabilityGate.Critical => summary.Critical > 0,
                VulnerabilityGate.MoreThanCurrent => await IsWorseAsync(env, item.Current, summary, ct),
                _ => false,
            };
            if (refused)
            {
                held.Add((item, $"{Images.ImageScanService.Describe(summary)} ({GateText(gate)})."));
            }
        }

        return held;
    }

    /// <summary>Whether the new image has more critical and high vulnerabilities than the one that runs now (its last scan, or a scan now).</summary>
    private async Task<bool> IsWorseAsync(DockerEnvironment env, string current, Images.ImageScanSummary candidate, CancellationToken ct)
    {
        var known = await _scans!.GetAsync(env.Id, current, ct);
        int? before = known is { Error: null } ? known.Critical + known.High : null;
        if (before is null)
        {
            var (ok, _, summary) = await _scans.ScanAsync(env, current, ct);
            before = ok && summary is not null ? summary.Critical + summary.High : 0;
        }

        return candidate.Critical + candidate.High > before;
    }

    public static string GateText(VulnerabilityGate gate) => gate switch
    {
        VulnerabilityGate.Any => "any known vulnerability blocks",
        VulnerabilityGate.CriticalHigh => "a critical or high one blocks",
        VulnerabilityGate.Critical => "a critical one blocks",
        VulnerabilityGate.MoreThanCurrent => "more critical or high ones than now block",
        _ => "not checked",
    };

    private UpdateNotice AddNotice(long stackId, PlannedUpdate item, bool failed, DateTime utcNow, bool notified)
    {
        var notice = new UpdateNotice { StackId = stackId, Service = item.Service, Target = Truncate(item.Target, 200), Failed = failed, NotifiedUtc = notified ? utcNow : null, FirstSeenUtc = utcNow };
        _db.UpdateNotices.Add(notice);
        return notice;
    }

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
