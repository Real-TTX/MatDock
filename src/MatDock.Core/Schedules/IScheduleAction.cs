using MatDock.Core.Entities;

namespace MatDock.Core.Schedules;

/// <summary>One kind of work a scheduled task can perform. Resolved by <see cref="Type"/> from the DI set.</summary>
public interface IScheduleAction
{
    ScheduleAction Type { get; }

    /// <summary>Runs the action for the given task and returns a short human-readable summary.</summary>
    Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default);
}
