using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Backups;

/// <summary>CRUD for backup schedules plus running due (or manually triggered) schedules.</summary>
public sealed class BackupScheduleService
{
    private readonly MatDockDbContext _db;
    private readonly BackupScheduleRunner _runner;
    private readonly ILogger<BackupScheduleService> _logger;

    public BackupScheduleService(MatDockDbContext db, BackupScheduleRunner runner, ILogger<BackupScheduleService> logger)
    {
        _db = db;
        _runner = runner;
        _logger = logger;
    }

    public Task<List<BackupSchedule>> GetAllAsync(CancellationToken ct = default)
        => _db.BackupSchedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

    public Task<BackupSchedule?> GetAsync(long id, CancellationToken ct = default)
        => _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<BackupSchedule> CreateAsync(BackupScheduleInput input, CancellationToken ct = default)
    {
        var schedule = new BackupSchedule();
        ApplyInput(schedule, input);
        _db.BackupSchedules.Add(schedule);
        await _db.SaveChangesAsync(ct);
        return schedule;
    }

    public async Task<bool> UpdateAsync(long id, BackupScheduleInput input, CancellationToken ct = default)
    {
        var schedule = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return false;
        }

        ApplyInput(schedule, input);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var schedule = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return false;
        }

        _db.BackupSchedules.Remove(schedule);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Runs one schedule immediately (manual trigger) and records the outcome.</summary>
    public async Task<string> RunNowAsync(long id, CancellationToken ct = default)
    {
        var schedule = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return "Schedule not found.";
        }

        var summary = await _runner.RunAsync(schedule, ct);
        schedule.LastRunAt = DateTime.UtcNow;
        schedule.LastStatus = summary;
        schedule.NextRunAt = SafeNext(schedule.Cron, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return summary;
    }

    /// <summary>Runs all schedules whose next run is due; returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Backfill next-run for enabled schedules that don't have one yet.
        var missing = await _db.BackupSchedules.Where(s => s.Enabled && s.NextRunAt == null).ToListAsync(ct);
        if (missing.Count > 0)
        {
            foreach (var s in missing)
            {
                s.NextRunAt = SafeNext(s.Cron, now);
            }
            await _db.SaveChangesAsync(ct);
        }

        var due = await _db.BackupSchedules
            .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var schedule in due)
        {
            // Advance next-run first so a long/failed run cannot cause a tight re-run loop.
            schedule.NextRunAt = SafeNext(schedule.Cron, now);
            await _db.SaveChangesAsync(ct);

            string summary;
            try
            {
                summary = await _runner.RunAsync(schedule, ct);
            }
            catch (Exception ex)
            {
                summary = $"Error: {ex.Message}";
                _logger.LogWarning(ex, "Scheduled backup '{Name}' failed.", schedule.Name);
            }

            schedule.LastRunAt = DateTime.UtcNow;
            schedule.LastStatus = summary;
            await _db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    private void ApplyInput(BackupSchedule schedule, BackupScheduleInput input)
    {
        // Collapse any line breaks so the name is always single-line (used in e-mail subjects, lists).
        schedule.Name = input.Name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        schedule.EnvironmentId = input.EnvironmentId;
        schedule.VolumesCsv = (input.VolumesCsv ?? string.Empty).Replace("\r", string.Empty).Trim();
        schedule.BackupTargetId = input.BackupTargetId;
        schedule.Cron = input.Cron.Trim();
        schedule.RetentionCount = Math.Max(0, input.RetentionCount);
        schedule.RetentionDays = Math.Max(0, input.RetentionDays);
        schedule.Enabled = input.Enabled;
        schedule.StopContainers = input.StopContainers;
        schedule.NextRunAt = input.Enabled ? SafeNext(schedule.Cron, DateTime.UtcNow) : null;
    }

    private static DateTime? SafeNext(string cron, DateTime fromUtc)
    {
        try
        {
            return CronSchedule.GetNextUtc(cron, fromUtc);
        }
        catch
        {
            return null;
        }
    }
}
