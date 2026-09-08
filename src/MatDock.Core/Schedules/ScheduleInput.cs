using MatDock.Core.Entities;

namespace MatDock.Core.Schedules;

/// <summary>Form carrier for creating/editing a scheduled task.</summary>
public sealed class ScheduleInput
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public ScheduleTrigger Trigger { get; set; } = ScheduleTrigger.Cron;
    public string? Cron { get; set; }
    public ScheduleEvent Event { get; set; } = ScheduleEvent.DeployFailed;
    public ScheduleAction Action { get; set; } = ScheduleAction.Summary;
    public long? EnvironmentId { get; set; }
    public bool OptionAll { get; set; }
    public bool OptionIncludeShares { get; set; }
    public bool NotifyOnResult { get; set; }
}
