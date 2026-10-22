using System.Collections.Concurrent;

namespace MatDock.Core.Updates;

/// <summary>
/// The last update check of each stack (by environment and compose project), so the Stacks list can mark the stacks with
/// newer images without asking the registries on every visit, and the overview shows what each rule would update. Filled by every check
/// (the Update page, the schedule action, the automation); in memory, a restart forgets it.
/// </summary>
public sealed class UpdateCheckCache
{
    private readonly ConcurrentDictionary<(long EnvironmentId, string Project), (StackUpdateReport Report, DateTime CheckedUtc)> _reports = new();
    private readonly ConcurrentDictionary<(long EnvironmentId, string Project), (StackUpdatePlan Plan, DateTime CheckedUtc)> _plans = new();

    public void Set(long environmentId, string project, StackUpdateReport report)
    {
        if (report.Error is null)
        {
            _reports[(environmentId, project)] = (report, DateTime.UtcNow);
        }
    }

    /// <summary>What the rule of the stack would update, as found at the last look.</summary>
    public void SetPlan(long environmentId, string project, StackUpdatePlan plan)
    {
        if (plan.Error is null)
        {
            _plans[(environmentId, project)] = (plan, DateTime.UtcNow);
        }
    }

    /// <summary>Forget a stack (after it was updated: its next check decides again).</summary>
    public void Clear(long environmentId, string project)
    {
        _reports.TryRemove((environmentId, project), out _);
        _plans.TryRemove((environmentId, project), out _);
    }

    /// <summary>The images of the stack with a newer version at the last check; empty when none or never checked.</summary>
    public IReadOnlyList<ImageUpdateStatus> Updates(long environmentId, string project)
        => _reports.TryGetValue((environmentId, project), out var entry)
            ? entry.Report.Images.Where(i => i.UpdateAvailable).ToList()
            : Array.Empty<ImageUpdateStatus>();

    /// <summary>The plan of the last look at the stack and when it was made; null when it was never looked at (or updated since).</summary>
    public (StackUpdatePlan Plan, DateTime CheckedUtc)? Plan(long environmentId, string project)
        => _plans.TryGetValue((environmentId, project), out var entry) ? entry : null;
}
