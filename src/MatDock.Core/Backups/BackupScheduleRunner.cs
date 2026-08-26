using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Backups;

/// <summary>Executes a single backup schedule: back up each selected volume, then prune by retention.</summary>
public sealed class BackupScheduleRunner
{
    private readonly MatDockDbContext _db;
    private readonly EnvironmentService _environmentService;
    private readonly VolumeBackupService _backupService;
    private readonly ILogger<BackupScheduleRunner> _logger;

    public BackupScheduleRunner(
        MatDockDbContext db,
        EnvironmentService environmentService,
        VolumeBackupService backupService,
        ILogger<BackupScheduleRunner> logger)
    {
        _db = db;
        _environmentService = environmentService;
        _backupService = backupService;
        _logger = logger;
    }

    /// <summary>Runs the schedule and returns a short status summary (also suitable for LastStatus).</summary>
    public async Task<string> RunAsync(BackupSchedule schedule, CancellationToken ct = default)
    {
        var env = await _environmentService.GetAsync(schedule.EnvironmentId, ct);
        if (env is null)
        {
            return "Environment nicht gefunden.";
        }

        if (!env.IsEnabled)
        {
            return "Environment ist deaktiviert – übersprungen.";
        }

        BackupTarget? target = null;
        if (schedule.BackupTargetId is { } tid)
        {
            target = await _db.BackupTargets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tid, ct);
            if (target is null)
            {
                return "Backup-Ziel wurde gelöscht.";
            }
        }

        var volumes = schedule.Volumes.ToList();
        if (volumes.Count == 0)
        {
            return "Keine Volumes ausgewählt.";
        }

        var ok = 0;
        var failures = new List<string>();
        foreach (var volume in volumes)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _backupService.BackupAsync(env, volume, target, schedule.Id, ct, stopContainers: schedule.StopContainers);
            if (result.Success)
            {
                ok++;
                await ApplyRetentionAsync(schedule, volume, ct);
            }
            else
            {
                failures.Add($"{volume}: {result.Message}");
            }
        }

        var summary = $"{ok}/{volumes.Count} Volumes gesichert";
        if (failures.Count > 0)
        {
            summary += " – " + string.Join("; ", failures.Take(3));
        }

        _logger.LogInformation("Schedule '{Name}' run: {Summary}", schedule.Name, summary);
        return summary;
    }

    private async Task ApplyRetentionAsync(BackupSchedule schedule, string volume, CancellationToken ct)
    {
        if (schedule.RetentionCount <= 0 && schedule.RetentionDays <= 0)
        {
            return;
        }

        // Only prune this schedule's OWN archives — never manual backups or other schedules' backups.
        var backups = await _db.VolumeBackups
            .Where(b => b.BackupScheduleId == schedule.Id && b.VolumeName == volume)
            .OrderByDescending(b => b.CreateDate)
            .ToListAsync(ct);

        var toDelete = new HashSet<long>();
        if (schedule.RetentionCount > 0 && backups.Count > schedule.RetentionCount)
        {
            foreach (var old in backups.Skip(schedule.RetentionCount))
            {
                toDelete.Add(old.Id);
            }
        }

        if (schedule.RetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-schedule.RetentionDays);
            foreach (var old in backups.Where(b => b.CreateDate < cutoff))
            {
                toDelete.Add(old.Id);
            }
        }

        foreach (var id in toDelete)
        {
            await _backupService.DeleteAsync(id, ct); // removes DB row + archive file
        }
    }
}
