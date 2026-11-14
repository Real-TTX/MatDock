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
    private readonly ConcurrentDictionary<(long EnvironmentId, string Project), string> _blocked = new();

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

    /// <summary>Why MatDock could not update a discovered project the last time it tried (its compose files are not on the host …); null when it could or never tried.</summary>
    public string? Blocked(long environmentId, string project) => _blocked.GetValueOrDefault((environmentId, project));

    /// <summary>Notes why a discovered project could not be updated by MatDock (null: it could).</summary>
    public void SetBlocked(long environmentId, string project, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            _blocked.TryRemove((environmentId, project), out _);
        }
        else
        {
            _blocked[(environmentId, project)] = reason;
        }
    }

    /// <summary>Forget every plan (a rule changed: what it makes of the last look is no longer what the plans say). The looks themselves stay.</summary>
    public void ClearPlans() => _plans.Clear();

    /// <summary>Forget a stack (after it was updated: its next check decides again).</summary>
    public void Clear(long environmentId, string project)
    {
        _reports.TryRemove((environmentId, project), out _);
        _plans.TryRemove((environmentId, project), out _);
        _blocked.TryRemove((environmentId, project), out _);
    }

    /// <summary>The images of the stack with a newer version at the last check; empty when none or never checked.</summary>
    public IReadOnlyList<ImageUpdateStatus> Updates(long environmentId, string project)
        => _reports.TryGetValue((environmentId, project), out var entry)
            ? entry.Report.Images.Where(i => i.UpdateAvailable).ToList()
            : Array.Empty<ImageUpdateStatus>();

    /// <summary>The last look at the stack and when it was taken; null when it was never looked at (or updated since).</summary>
    public (StackUpdateReport Report, DateTime CheckedUtc)? Report(long environmentId, string project)
        => _reports.TryGetValue((environmentId, project), out var entry) ? entry : null;

    /// <summary>The plan of the last look at the stack and when it was made; null when it was never looked at (or updated since).</summary>
    public (StackUpdatePlan Plan, DateTime CheckedUtc)? Plan(long environmentId, string project)
        => _plans.TryGetValue((environmentId, project), out var entry) ? entry : null;
}
