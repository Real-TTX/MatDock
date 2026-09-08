namespace MatDock.Core.Entities;

/// <summary>
/// A generic scheduled/automated task: a <see cref="ScheduleTrigger"/> (cron time or an internal event)
/// paired with a <see cref="ScheduleAction"/> (prune, summary report, …). Runs are recorded on the row.
/// Mapped to the <c>ScheduledTask</c> table.
/// </summary>
public class ScheduledTask : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ScheduleTrigger Trigger { get; set; } = ScheduleTrigger.Cron;

    /// <summary>Cron expression (5-field, UTC) when <see cref="Trigger"/> is Cron.</summary>
    public string? Cron { get; set; }

    /// <summary>The event that fires the task when <see cref="Trigger"/> is Event.</summary>
    public ScheduleEvent Event { get; set; } = ScheduleEvent.DeployFailed;

    public ScheduleAction Action { get; set; } = ScheduleAction.Summary;

    /// <summary>Target environment, or null to apply to all enabled environments.</summary>
    public long? EnvironmentId { get; set; }

    /// <summary>Action-specific options as JSON (e.g. prune "all"/"includeShares"), or null.</summary>
    public string? OptionsJson { get; set; }

    /// <summary>Send a notification with the run outcome via the configured channels.</summary>
    public bool NotifyOnResult { get; set; }

    public DateTime? LastRunAt { get; set; }

    public DateTime? NextRunAt { get; set; }

    public string? LastStatus { get; set; }
}
