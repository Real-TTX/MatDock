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

    public ScheduleService(
        MatDockDbContext db,
        IEnumerable<IScheduleAction> actions,
        INotificationService notifications,
        ILogger<ScheduleService> logger)
    {
        _db = db;
        _actions = actions;
        _notifications = notifications;
        _logger = logger;
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
            var summary = await RunAndNotifyAsync(task, ct);
            task.LastRunAt = DateTime.UtcNow;
            task.LastStatus = string.IsNullOrEmpty(detail) ? summary : $"[{detail}] {summary}";
            await _db.SaveChangesAsync(ct);
        }

        return tasks.Count;
    }

    private async Task<string> RunAndNotifyAsync(ScheduledTask task, CancellationToken ct)
    {
        bool ok;
        string summary;
        var action = _actions.FirstOrDefault(a => a.Type == task.Action);
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
                await _notifications.NotifyAsync($"MatDock schedule: {task.Name} — {(ok ? "OK" : "Error")}", summary, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Result notification for schedule '{Name}' failed.", task.Name);
            }
        }

        return summary;
    }

    private void Apply(ScheduledTask task, ScheduleInput input)
    {
        task.Name = input.Name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        task.Enabled = input.Enabled;
        task.Trigger = input.Trigger;
        task.Cron = input.Trigger == ScheduleTrigger.Cron ? input.Cron?.Trim() : null;
        task.Event = input.Event;
        task.Action = input.Action;
        task.EnvironmentId = input.EnvironmentId is > 0 ? input.EnvironmentId : null;
        task.NotifyOnResult = input.NotifyOnResult;
        task.OptionsJson = new ScheduleOptions
        {
            All = input.OptionAll,
            IncludeShares = input.OptionIncludeShares,
            BackupScheduleId = input.Action == ScheduleAction.Backup ? input.BackupScheduleId : null,
            SyncJobId = input.Action == ScheduleAction.Sync ? input.SyncJobId : null,
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
        if (input.Action == ScheduleAction.Backup && input.BackupScheduleId is not > 0)
        {
            return "Please select a backup definition to run.";
        }
        if (input.Action == ScheduleAction.Sync && input.SyncJobId is not > 0)
        {
            return "Please select a sync job to run.";
        }
        return null;
    }

    private static DateTime? SafeNext(string? cron, DateTime fromUtc)
    {
        if (string.IsNullOrWhiteSpace(cron)) { return null; }
        try { return CronSchedule.GetNextUtc(cron, fromUtc); }
        catch { return null; }
    }
}
