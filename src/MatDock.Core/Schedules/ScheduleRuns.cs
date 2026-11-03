using System.Collections.Concurrent;

namespace MatDock.Core.Schedules;

/// <summary>
/// The runs of schedules that are going on now (a backup, a sync, an update): what runs, since when, and the switch that stops it. One run per schedule
/// at a time: a second start while one runs is refused. A run that is cancelled gets its token cancelled; the action ends where it can (a transfer stops,
/// the command on the host is ended) and the run is recorded as cancelled.
/// </summary>
public sealed class ScheduleRuns
{
    private readonly ConcurrentDictionary<long, Run> _runs = new();

    public sealed record Run(long TaskId, string Name, long? EnvironmentId, DateTime StartedUtc, CancellationTokenSource Cancel)
    {
        public bool Cancelling => Cancel.IsCancellationRequested;
    }

    /// <summary>What runs now, the oldest first.</summary>
    public IReadOnlyList<Run> Running => _runs.Values.OrderBy(r => r.StartedUtc).ToList();

    public Run? Of(long taskId) => _runs.TryGetValue(taskId, out var run) ? run : null;

    /// <summary>Starts the run of a schedule, or null when it runs already. Dispose the handle when the run ends.</summary>
    public Handle? Begin(long taskId, string name, long? environmentId, CancellationToken outer)
    {
        var run = new Run(taskId, name, environmentId, DateTime.UtcNow, CancellationTokenSource.CreateLinkedTokenSource(outer));
        return _runs.TryAdd(taskId, run) ? new Handle(this, run) : null;
    }

    /// <summary>Asks the run of a schedule to stop; false when none runs.</summary>
    public bool Cancel(long taskId)
    {
        if (!_runs.TryGetValue(taskId, out var run))
        {
            return false;
        }

        try { run.Cancel.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public sealed class Handle(ScheduleRuns owner, Run run) : IDisposable
    {
        public CancellationToken Token => run.Cancel.Token;

        /// <summary>Whether it was stopped by <see cref="Cancel"/> (not by the end of MatDock).</summary>
        public bool Cancelled => run.Cancel.IsCancellationRequested;

        public void Dispose()
        {
            owner._runs.TryRemove(new KeyValuePair<long, Run>(run.TaskId, run));
            run.Cancel.Dispose();
        }
    }
}
