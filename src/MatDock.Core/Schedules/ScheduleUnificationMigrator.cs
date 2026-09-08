using MatDock.Core.Backups;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Schedules;

/// <summary>
/// One-time, idempotent unification: mirrors existing cron-based BackupSchedules and SyncJobs into generic
/// <see cref="ScheduledTask"/>s (referencing the original definition) so the single Schedules runner drives
/// their timing. The originals are left intact (reversible); duplicates are prevented via
/// <see cref="ScheduledTask.SourceKind"/>/<see cref="ScheduledTask.SourceId"/>. Idempotent: safe to run on
/// every startup — it only creates tasks that don't exist yet.
/// </summary>
public sealed class ScheduleUnificationMigrator
{
    private const string BackupKind = "backup";
    private const string SyncKind = "sync";

    private readonly MatDockDbContext _db;
    private readonly ILogger<ScheduleUnificationMigrator> _logger;

    public ScheduleUnificationMigrator(MatDockDbContext db, ILogger<ScheduleUnificationMigrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        var created = 0;
        var now = DateTime.UtcNow;

        var existing = await _db.ScheduledTasks
            .Where(s => s.SourceKind != null)
            .Select(s => new { s.SourceKind, s.SourceId })
            .ToListAsync(ct);
        var have = existing
            .Where(x => x.SourceId != null)
            .Select(x => (x.SourceKind, x.SourceId!.Value))
            .ToHashSet();

        // --- Backup schedules -> Backup tasks ---
        var backups = await _db.BackupSchedules.ToListAsync(ct);
        foreach (var b in backups)
        {
            if (have.Contains((BackupKind, b.Id))) { continue; }

            _db.ScheduledTasks.Add(new ScheduledTask
            {
                Name = $"Backup: {b.Name}",
                Enabled = b.Enabled,
                Trigger = ScheduleTrigger.Cron,
                Cron = b.Cron,
                Action = ScheduleAction.Backup,
                EnvironmentId = b.EnvironmentId,
                OptionsJson = new ScheduleOptions { BackupScheduleId = b.Id }.ToJson(),
                NextRunAt = b.Enabled ? SafeNext(b.Cron, now) : null,
                SourceKind = BackupKind,
                SourceId = b.Id,
            });
            created++;
        }

        // --- Sync jobs (cron-enabled) -> Sync tasks ---
        var syncs = await _db.SyncJobs.Where(s => s.ScheduleEnabled && s.Cron != null).ToListAsync(ct);
        foreach (var s in syncs)
        {
            if (have.Contains((SyncKind, s.Id))) { continue; }

            _db.ScheduledTasks.Add(new ScheduledTask
            {
                Name = $"Sync: {s.Name}",
                Enabled = true,
                Trigger = ScheduleTrigger.Cron,
                Cron = s.Cron,
                Action = ScheduleAction.Sync,
                EnvironmentId = null,
                OptionsJson = new ScheduleOptions { SyncJobId = s.Id }.ToJson(),
                NextRunAt = SafeNext(s.Cron, now),
                SourceKind = SyncKind,
                SourceId = s.Id,
            });
            created++;
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Schedule unification: created {Count} scheduled task(s) from existing backup/sync definitions.", created);
        }

        return created;
    }

    private static DateTime? SafeNext(string? cron, DateTime fromUtc)
    {
        if (string.IsNullOrWhiteSpace(cron)) { return null; }
        try { return CronSchedule.GetNextUtc(cron, fromUtc); }
        catch { return null; }
    }
}
