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
    public string? VolumesCsv { get; set; }
    public long? BackupTargetId { get; set; }
    public int RetentionCount { get; set; }
    public int RetentionDays { get; set; }
    public bool StopContainers { get; set; }
    public bool NotifyOnResult { get; set; }

    /// <summary>BundleBackup: the stack to back up (empty = every managed stack of the owner); UpdateStacks: the stack to check.</summary>
    public string? BundleStack { get; set; }

    /// <summary>UpdateStacks: pull and redeploy the stacks with newer images (false = only check and notify).</summary>
    public bool UpdateApply { get; set; }

    /// <summary>UpdateStacks: bundle backup of a stack before its update.</summary>
    public bool UpdateBackupFirst { get; set; }

    /// <summary>Owner of a task created through the API (e.g. <c>mattiverse</c>). <c>null</c> keeps the stored value, so
    /// a save from the Schedules page never drops it; an empty string clears it.</summary>
    public string? ManagedBy { get; set; }
}
