using MatDock.Core.Backups;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Schedules;

/// <summary>CRUD for scheduled tasks plus running due (cron) tasks and manual/event dispatch.</summary>
public sealed class ScheduleService
{
    /// <summary>SourceKind values linking a task back to the BackupSchedule/SyncJob it mirrors.</summary>
    public const string BackupSource = "backup";
    public const string SyncSource = "sync";

    private readonly MatDockDbContext _db;
    private readonly IEnumerable<IScheduleAction> _actions;
    private readonly INotificationService _notifications;
    private readonly ILogger<ScheduleService> _logger;
    private readonly Security.ISecretProtector? _secrets;

    public ScheduleService(
        MatDockDbContext db,
        IEnumerable<IScheduleAction> actions,
        INotificationService notifications,
        ILogger<ScheduleService> logger,
        Security.ISecretProtector? secrets = null)
    {
        _db = db;
        _actions = actions;
        _notifications = notifications;
        _logger = logger;
        _secrets = secrets;
    }

    public Task<List<ScheduledTask>> GetAllAsync(CancellationToken ct = default)
        => _db.ScheduledTasks.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

    public Task<ScheduledTask?> GetAsync(long id, CancellationToken ct = default)
        => _db.ScheduledTasks.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(ScheduleInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null) { return (false, error, 0); }

        var task = new ScheduledTask();
        Apply(task, input);
        if (MissingPassphrase(task) is { } missing) { return (false, missing, 0); }
        _db.ScheduledTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        return (true, "Schedule saved.", task.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, ScheduleInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null) { return (false, error); }

        var task = await _db.ScheduledTasks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (task is null) { return (false, "Schedule not found."); }

        Apply(task, input);
        if (MissingPassphrase(task) is { } missing) { return (false, missing); }
        await _db.SaveChangesAsync(ct);
        return (true, "Schedule saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (task is null) { return false; }

        _db.ScheduledTasks.Remove(task);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Runs one task immediately (manual trigger) and records the outcome.</summary>
    public async Task<string> RunNowAsync(long id, CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (task is null) { return "Schedule not found."; }

        var summary = await RunAndNotifyAsync(task, ct);
        task.LastRunAt = DateTime.UtcNow;
        task.LastStatus = summary;
        if (task.Trigger == ScheduleTrigger.Cron)
        {
            task.NextRunAt = SafeNext(task.Cron, DateTime.UtcNow);
        }
        await _db.SaveChangesAsync(ct);
        return summary;
    }

    /// <summary>Runs all cron tasks whose next run is due; returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Backfill next-run for enabled cron tasks that don't have one yet.
        var missing = await _db.ScheduledTasks
            .Where(s => s.Enabled && s.Trigger == ScheduleTrigger.Cron && s.NextRunAt == null && s.Cron != null)
            .ToListAsync(ct);
        if (missing.Count > 0)
        {
            foreach (var s in missing) { s.NextRunAt = SafeNext(s.Cron, now); }
            await _db.SaveChangesAsync(ct);
        }

        var due = await _db.ScheduledTasks
            .Where(s => s.Enabled && s.Trigger == ScheduleTrigger.Cron && s.NextRunAt != null && s.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var task in due)
        {
            // Advance next-run first so a long/failed run cannot cause a tight re-run loop.
            task.NextRunAt = SafeNext(task.Cron, now);
            await _db.SaveChangesAsync(ct);

            var summary = await RunAndNotifyAsync(task, ct);
            task.LastRunAt = DateTime.UtcNow;
            task.LastStatus = summary;
            await _db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    /// <summary>Creates or updates the ScheduledTask that mirrors a BackupSchedule/SyncJob so the unified
    /// runner drives its timing live (called by those definition services on save).</summary>
    public async Task UpsertSourceTaskAsync(string kind, long sourceId, string name, long? environmentId,
        string cron, bool enabled, ScheduleAction action, ScheduleOptions options, CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks.FirstOrDefaultAsync(t => t.SourceKind == kind && t.SourceId == sourceId, ct);
        if (task is null)
        {
            task = new ScheduledTask { SourceKind = kind, SourceId = sourceId };
            _db.ScheduledTasks.Add(task);
        }

        task.Name = name;
        task.Trigger = ScheduleTrigger.Cron;
        task.Cron = cron;
        task.Action = action;
        task.EnvironmentId = environmentId;
        task.Enabled = enabled;
        task.OptionsJson = options.ToJson();
        task.NextRunAt = enabled ? SafeNext(cron, DateTime.UtcNow) : null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Removes the mirrored ScheduledTask for a deleted/disabled source, if any.</summary>
    public async Task RemoveSourceTaskAsync(string kind, long sourceId, CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks.FirstOrDefaultAsync(t => t.SourceKind == kind && t.SourceId == sourceId, ct);
        if (task is not null)
        {
            _db.ScheduledTasks.Remove(task);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Runs every enabled event-triggered task subscribed to <paramref name="evt"/> (matching the
    /// environment, or global). Returns how many ran.</summary>
    public async Task<int> DispatchEventAsync(ScheduleEvent evt, long? environmentId, string? detail, CancellationToken ct = default)
    {
        var tasks = await _db.ScheduledTasks
            .Where(s => s.Enabled && s.Trigger == ScheduleTrigger.Event && s.Event == evt
                        && (s.EnvironmentId == null || s.EnvironmentId == environmentId))
            .ToListAsync(ct);

        foreach (var task in tasks)
        {
            var summary = await RunAndNotifyAsync(task, ct, evt, detail);
            task.LastRunAt = DateTime.UtcNow;
            task.LastStatus = string.IsNullOrEmpty(detail) || task.Action == ScheduleAction.Notify ? summary : $"[{detail}] {summary}";
            await _db.SaveChangesAsync(ct);
        }

        return tasks.Count;
    }

    /// <summary>Whether an enabled schedule waits for one of these events (so nobody watches for events no one wants).</summary>
    public Task<bool> HasEventSchedulesAsync(IReadOnlyCollection<ScheduleEvent> events, CancellationToken ct = default)
        => _db.ScheduledTasks.AnyAsync(s => s.Enabled && s.Trigger == ScheduleTrigger.Event && events.Contains(s.Event), ct);

    /// <summary>The words of an event in a notification.</summary>
    public static string EventLabel(ScheduleEvent evt) => evt switch
    {
        ScheduleEvent.DeployFailed => "Deploy failed",
        ScheduleEvent.BackupFailed => "Backup failed",
        ScheduleEvent.SyncFailed => "Sync failed",
        ScheduleEvent.EnvironmentOffline => "Environment offline",
        ScheduleEvent.ContainerDied => "Container died",
        ScheduleEvent.ContainerUnhealthy => "Container unhealthy",
        ScheduleEvent.HostThreshold => "Host limit",
        ScheduleEvent.ContainerRestartLoop => "Restart loop",
        _ => evt.ToString(),
    };

    /// <summary>Whether an enabled schedule sends a notification on this event.</summary>
    public Task<bool> NotifiesOnAsync(ScheduleEvent evt, CancellationToken ct = default)
        => _db.ScheduledTasks.AnyAsync(s => s.Enabled && s.Trigger == ScheduleTrigger.Event && s.Event == evt && s.Action == ScheduleAction.Notify, ct);

    /// <summary>
    /// Sends a notification on an event or stops it: switched on, the notification schedules of the event are enabled (one "Alert: …" is created when
    /// there is none); switched off, they are disabled, never deleted.
    /// </summary>
    public async Task SetNotifyOnAsync(ScheduleEvent evt, bool on, CancellationToken ct = default)
    {
        var tasks = await _db.ScheduledTasks
            .Where(s => s.Trigger == ScheduleTrigger.Event && s.Event == evt && s.Action == ScheduleAction.Notify)
            .ToListAsync(ct);
        if (on && tasks.Count == 0)
        {
            await CreateAsync(new ScheduleInput { Name = $"Alert: {EventLabel(evt)}", Trigger = ScheduleTrigger.Event, Event = evt, Action = ScheduleAction.Notify }, ct);
            return;
        }

        foreach (var task in tasks)
        {
            task.Enabled = on;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> RunAndNotifyAsync(ScheduledTask task, CancellationToken ct, ScheduleEvent? evt = null, string? detail = null)
    {
        bool ok;
        string summary;
        var action = _actions.FirstOrDefault(a => a.Type == task.Action);
        if (task.Action == ScheduleAction.Notify)
        {
            // The event is the message; without one (Run now) it is a test of the channels.
            var subject = evt is { } e ? $"MatDock: {EventLabel(e)}" : $"MatDock schedule: {task.Name} (test)";
            var body = detail ?? (evt is null ? "A test of the schedule's notification." : EventLabel(evt.Value));
            (ok, var sent) = await _notifications.NotifyAsync(subject, body, ct);
            task.LastRunOk = ok;
            return ok ? body : $"Error: {sent}";
        }

        if (action is null)
        {
            ok = false;
            summary = $"No handler for action {task.Action}.";
        }
        else
        {
            try
            {
                (ok, summary) = await action.ExecuteAsync(task, ct);
            }
            catch (Exception ex)
            {
                ok = false;
                summary = $"Error: {ex.Message}";
                _logger.LogWarning(ex, "Scheduled task '{Name}' ({Action}) failed.", task.Name, task.Action);
            }
        }

        if (task.NotifyOnResult)
        {
            try
            {
                await _notifications.NotifyAsync($"MatDock schedule: {task.Name} — {(ok ? "OK" : "Error")}",
                    string.IsNullOrEmpty(detail) ? summary : $"{detail}\n\n{summary}", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Result notification for schedule '{Name}' failed.", task.Name);
            }
        }

        // Recorded with the run (the callers save it together with LastRunAt / LastStatus).
        task.LastRunOk = ok;
        return summary;
    }

    /// <summary>The bundle schedules (BundleBackup tasks), by name.</summary>
    public Task<List<ScheduledTask>> GetBundleSchedulesAsync(CancellationToken ct = default)
        => _db.ScheduledTasks.AsNoTracking()
            .Where(s => s.Action == ScheduleAction.BundleBackup)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    /// <summary>The bundle schedule with exactly this name (the identity used by the API), or null.</summary>
    public Task<ScheduledTask?> FindBundleScheduleAsync(string name, CancellationToken ct = default)
        => _db.ScheduledTasks
            .Where(s => s.Action == ScheduleAction.BundleBackup && s.Name == name)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(ct);

    private void Apply(ScheduledTask task, ScheduleInput input)
    {
        var isBackup = input.Action is ScheduleAction.Backup or ScheduleAction.BundleBackup;
        var self = input.Action == ScheduleAction.SelfBackup;
        // A blank passphrase keeps the stored one (like the other secrets).
        var passphrase = !self ? null
            : !string.IsNullOrEmpty(input.SelfBackupPassphrase) && _secrets is not null ? _secrets.Protect(input.SelfBackupPassphrase)
            : ScheduleOptions.Parse(task.OptionsJson).SelfBackupPassphrase;
        task.Name = input.Name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        task.Enabled = input.Enabled;
        task.Trigger = input.Trigger;
        task.Cron = input.Trigger == ScheduleTrigger.Cron ? input.Cron?.Trim() : null;
        task.Event = input.Event;
        task.Action = input.Action;
        task.EnvironmentId = input.EnvironmentId is > 0 ? input.EnvironmentId : null;
        task.NotifyOnResult = input.NotifyOnResult;
        if (input.ManagedBy is not null)
        {
            task.ManagedBy = string.IsNullOrWhiteSpace(input.ManagedBy) ? null : input.ManagedBy.Trim();
        }
        task.OptionsJson = new ScheduleOptions
        {
            All = input.OptionAll,
            IncludeShares = input.OptionIncludeShares,
            VolumesCsv = input.Action == ScheduleAction.Backup ? input.VolumesCsv : null,
            BackupTargetId = isBackup || self || input.Action == ScheduleAction.ResticMaintenance ? input.BackupTargetId : null,
            RetentionCount = isBackup || self ? input.RetentionCount : 0,
            RetentionDays = isBackup ? input.RetentionDays : 0,
            StopContainers = isBackup && input.StopContainers,
            BundleStack = input.Action is ScheduleAction.BundleBackup or ScheduleAction.UpdateStacks or ScheduleAction.StartStacks or ScheduleAction.StopStacks && !string.IsNullOrWhiteSpace(input.BundleStack)
                ? input.BundleStack.Trim()
                : null,
            UpdateApply = input.Action == ScheduleAction.UpdateStacks && input.UpdateApply,
            UpdateBackupFirst = input.Action == ScheduleAction.UpdateStacks && input.UpdateApply && input.UpdateBackupFirst,
            UpdateRollback = input.Action == ScheduleAction.UpdateStacks && input.UpdateApply && input.UpdateRollback,
            SelfBackupPassphrase = passphrase,
        }.ToJson();
        task.NextRunAt = input.Enabled && input.Trigger == ScheduleTrigger.Cron
            ? SafeNext(task.Cron, DateTime.UtcNow)
            : null;
    }

    private static string? Validate(ScheduleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }
        if (input.Trigger == ScheduleTrigger.Cron && !CronSchedule.IsValid(input.Cron))
        {
            return "Please enter a valid cron expression (5 fields, UTC).";
        }
        if (input.Action == ScheduleAction.Backup)
        {
            if (input.EnvironmentId is not > 0)
            {
                return "Please select a target environment for the backup.";
            }
            if (string.IsNullOrWhiteSpace(input.VolumesCsv))
            {
                return "Please enter at least one volume to back up.";
            }
        }
        if (input.Action == ScheduleAction.BundleBackup
            && !string.IsNullOrWhiteSpace(input.BundleStack) && !Stacks.StackCommands.IsValidName(input.BundleStack.Trim()))
        {
            return "Invalid stack name (lowercase, a-z 0-9 _ -, must start alphanumeric).";
        }
        if (input.Action == ScheduleAction.Notify && input.Trigger != ScheduleTrigger.Event)
        {
            return "A notification is sent for an event: choose the trigger Event.";
        }
        if (input.Action == ScheduleAction.UpdateStacks
            && !string.IsNullOrWhiteSpace(input.BundleStack) && !Stacks.StackCommands.IsValidName(input.BundleStack.Trim()))
        {
            return "Invalid stack name (lowercase, a-z 0-9 _ -, must start alphanumeric).";
        }
        if (input.Action is ScheduleAction.StartStacks or ScheduleAction.StopStacks)
        {
            var names = StackNames(input.BundleStack);
            if (names.Count == 0)
            {
                return "Name the stack(s) to start or stop (comma-separated).";
            }

            if (names.FirstOrDefault(n => !Stacks.StackCommands.IsValidName(n)) is { } bad)
            {
                return $"Invalid stack name '{bad}' (lowercase, a-z 0-9 _ -, must start alphanumeric).";
            }
        }
        if (input.Action == ScheduleAction.SelfBackup && !string.IsNullOrEmpty(input.SelfBackupPassphrase)
            && SelfBackupService.PassphraseProblem(input.SelfBackupPassphrase) is { } weak)
        {
            return weak;
        }
        if (input.RetentionCount < 0 || input.RetentionDays < 0)
        {
            return "Retention cannot be negative.";
        }
        return null;
    }

    private static string? MissingPassphrase(ScheduledTask task)
        => task.Action == ScheduleAction.SelfBackup && ScheduleOptions.Parse(task.OptionsJson).SelfBackupPassphrase is null
            ? "Please enter the passphrase the backups are encrypted with."
            : null;

    /// <summary>The stack names of a start or stop task (comma-separated, trimmed, each once).</summary>
    public static IReadOnlyList<string> StackNames(string? csv)
        => (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    private static DateTime? SafeNext(string? cron, DateTime fromUtc)
    {
        if (string.IsNullOrWhiteSpace(cron)) { return null; }
        try { return CronSchedule.GetNextUtc(cron, fromUtc); }
        catch { return null; }
    }
}
