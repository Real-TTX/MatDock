using System.Collections.Concurrent;

namespace MatDock.Core.Updates;

/// <summary>
/// The last update check of each stack (by environment and compose project), so the Stacks list can mark the stacks with
/// newer images without asking the registries on every visit. Filled by every check (the Update page, the schedule action);
/// in memory, a restart forgets it.
/// </summary>
public sealed class UpdateCheckCache
{
    private readonly ConcurrentDictionary<(long EnvironmentId, string Project), (StackUpdateReport Report, DateTime CheckedUtc)> _reports = new();

    public void Set(long environmentId, string project, StackUpdateReport report)
    {
        if (report.Error is null)
        {
            _reports[(environmentId, project)] = (report, DateTime.UtcNow);
        }
    }

    /// <summary>Forget a stack (after it was updated: its next check decides again).</summary>
    public void Clear(long environmentId, string project) => _reports.TryRemove((environmentId, project), out _);

    /// <summary>The images of the stack with a newer version at the last check; empty when none or never checked.</summary>
    public IReadOnlyList<ImageUpdateStatus> Updates(long environmentId, string project)
        => _reports.TryGetValue((environmentId, project), out var entry)
            ? entry.Report.Images.Where(i => i.UpdateAvailable).ToList()
            : Array.Empty<ImageUpdateStatus>();
}
