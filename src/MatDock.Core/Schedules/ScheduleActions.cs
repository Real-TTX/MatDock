using System.Text;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Sync;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Schedules;

/// <summary>Shared helper: resolves the environments a task targets (one, or all enabled).</summary>
public abstract class ScheduleActionBase
{
    protected readonly EnvironmentService Environments;
    protected readonly IEnvironmentConnectionService Connection;

    protected ScheduleActionBase(EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        Environments = environments;
        Connection = connection;
    }

    protected async Task<IReadOnlyList<DockerEnvironment>> TargetsAsync(ScheduledTask task, CancellationToken ct)
    {
        if (task.EnvironmentId is > 0)
        {
            var env = await Environments.GetAsync(task.EnvironmentId.Value, ct);
            return env is { IsEnabled: true } ? new[] { env } : Array.Empty<DockerEnvironment>();
        }

        return await Environments.GetEnabledAsync(ct);
    }
}

/// <summary>Removes unused volumes (local by default; network shares only when opted in) per target environment.</summary>
public sealed class PruneVolumesAction : ScheduleActionBase, IScheduleAction
{
    public PruneVolumesAction(EnvironmentService e, IEnvironmentConnectionService c) : base(e, c) { }

    public ScheduleAction Type => ScheduleAction.PruneVolumes;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var opt = ScheduleOptions.Parse(task.OptionsJson);
        var envs = await TargetsAsync(task, ct);
        if (envs.Count == 0) { return (false, "No target environment."); }

        var removed = 0; var errors = new List<string>();
        foreach (var env in envs)
        {
            try
            {
                var settings = Environments.BuildSettings(env);
                var unused = await Connection.ListUnusedVolumesDetailedAsync(settings, ct);
                var names = unused.Where(u => opt.IncludeShares || !u.IsNetworkShare).Select(u => u.Name).ToList();
                if (names.Count > 0)
                {
                    var result = await Connection.RemoveVolumesAsync(settings, names, ct);
                    removed += result.Removed;
                }
            }
            catch (Exception ex) { errors.Add($"{env.Name}: {ex.Message}"); }
        }

        return Result($"Pruned {removed} volume(s) across {envs.Count} environment(s).", errors);
    }

    internal static (bool, string) Result(string summary, List<string> errors)
        => errors.Count == 0 ? (true, summary) : (false, summary + " Errors: " + string.Join("; ", errors));
}

/// <summary>Prunes dangling (or all unused) images per target environment.</summary>
public sealed class PruneImagesAction : ScheduleActionBase, IScheduleAction
{
    public PruneImagesAction(EnvironmentService e, IEnvironmentConnectionService c) : base(e, c) { }

    public ScheduleAction Type => ScheduleAction.PruneImages;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var opt = ScheduleOptions.Parse(task.OptionsJson);
        var envs = await TargetsAsync(task, ct);
        if (envs.Count == 0) { return (false, "No target environment."); }

        var removed = 0; var errors = new List<string>();
        foreach (var env in envs)
        {
            try
            {
                var result = await Connection.PruneImagesAsync(Environments.BuildSettings(env), opt.All, ct);
                if (result.Success) { removed += result.Removed; }
                else { errors.Add($"{env.Name}: {result.Detail}"); }
            }
            catch (Exception ex) { errors.Add($"{env.Name}: {ex.Message}"); }
        }

        return PruneVolumesAction.Result($"Pruned {removed} image(s) across {envs.Count} environment(s).", errors);
    }
}

/// <summary>Prunes unused custom networks per target environment.</summary>
public sealed class PruneNetworksAction : ScheduleActionBase, IScheduleAction
{
    public PruneNetworksAction(EnvironmentService e, IEnvironmentConnectionService c) : base(e, c) { }

    public ScheduleAction Type => ScheduleAction.PruneNetworks;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var envs = await TargetsAsync(task, ct);
        if (envs.Count == 0) { return (false, "No target environment."); }

        var removed = 0; var errors = new List<string>();
        foreach (var env in envs)
        {
            try
            {
                var result = await Connection.PruneNetworksAsync(Environments.BuildSettings(env), ct);
                if (result.Success) { removed += result.Removed; }
                else { errors.Add($"{env.Name}: {result.Detail}"); }
            }
            catch (Exception ex) { errors.Add($"{env.Name}: {ex.Message}"); }
        }

        return PruneVolumesAction.Result($"Pruned {removed} network(s) across {envs.Count} environment(s).", errors);
    }
}

/// <summary>Sends a status summary (per-environment running/CPU/RAM/Disk) via the notification channels.</summary>
public sealed class SummaryAction : ScheduleActionBase, IScheduleAction
{
    private readonly INotificationService _notifications;

    public SummaryAction(EnvironmentService e, IEnvironmentConnectionService c, INotificationService notifications) : base(e, c)
        => _notifications = notifications;

    public ScheduleAction Type => ScheduleAction.Summary;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var envs = await TargetsAsync(task, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"MatDock status summary — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        foreach (var env in envs)
        {
            try
            {
                var st = await Connection.GetHostStatsAsync(Environments.BuildSettings(env), ct);
                sb.AppendLine($"- {env.Name}: {st.RunningContainers ?? 0} running · CPU {st.CpuPercent:0}% · RAM {st.MemPercent:0}% · Disk {st.DiskPercent:0}%");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- {env.Name}: unreachable ({ex.Message})");
            }
        }

        var (ok, message) = await _notifications.NotifyAsync("MatDock status summary", sb.ToString(), ct);
        return ok ? (true, $"Summary sent for {envs.Count} environment(s).") : (false, message);
    }
}

/// <summary>Checks target environments (reachability + disk/RAM thresholds) and sends an alert on problems.</summary>
public sealed class HealthAlertAction : ScheduleActionBase, IScheduleAction
{
    private const int ThresholdPercent = 90;

    private readonly INotificationService _notifications;

    public HealthAlertAction(EnvironmentService e, IEnvironmentConnectionService c, INotificationService notifications) : base(e, c)
        => _notifications = notifications;

    public ScheduleAction Type => ScheduleAction.HealthAlert;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var envs = await TargetsAsync(task, ct);
        var problems = new List<string>();

        foreach (var env in envs)
        {
            try
            {
                var st = await Connection.GetHostStatsAsync(Environments.BuildSettings(env), ct);
                if (st.DiskPercent >= ThresholdPercent) { problems.Add($"{env.Name}: disk {st.DiskPercent:0}%"); }
                if (st.MemPercent >= ThresholdPercent) { problems.Add($"{env.Name}: RAM {st.MemPercent:0}%"); }
            }
            catch (Exception ex)
            {
                problems.Add($"{env.Name}: unreachable ({ex.Message})");
            }
        }

        if (problems.Count == 0)
        {
            return (true, $"All {envs.Count} environment(s) healthy.");
        }

        var body = "MatDock health alert:\n\n- " + string.Join("\n- ", problems);
        await _notifications.NotifyAsync("MatDock health alert", body, ct);
        return (false, $"{problems.Count} problem(s): {string.Join("; ", problems)}");
    }
}

/// <summary>Backs up the configured volumes of the target environment (self-contained: volumes/target/
/// retention live in the task options) and prunes this task's own archives by retention.</summary>
public sealed class BackupAction : IScheduleAction
{
    private readonly MatDockDbContext _db;
    private readonly EnvironmentService _environments;
    private readonly VolumeBackupService _backup;

    public BackupAction(MatDockDbContext db, EnvironmentService environments, VolumeBackupService backup)
    {
        _db = db;
        _environments = environments;
        _backup = backup;
    }

    public ScheduleAction Type => ScheduleAction.Backup;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        if (task.EnvironmentId is not > 0)
        {
            return (false, "No environment selected for backup.");
        }

        var env = await _environments.GetAsync(task.EnvironmentId.Value, ct);
        if (env is null || !env.IsEnabled)
        {
            return (false, "Environment not available (disabled or deleted).");
        }

        var opt = ScheduleOptions.Parse(task.OptionsJson);
        var volumes = (opt.VolumesCsv ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (volumes.Count == 0)
        {
            return (false, "No volumes selected.");
        }

        BackupTarget? target = null;
        if (opt.BackupTargetId is { } tid)
        {
            target = await _db.BackupTargets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tid, ct);
            if (target is null)
            {
                return (false, "Backup target has been deleted.");
            }
        }

        var ok = 0;
        var failures = new List<string>();
        foreach (var volume in volumes)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _backup.BackupAsync(env, volume, target, scheduleId: null, ct,
                stopContainers: opt.StopContainers, scheduledTaskId: task.Id);
            if (result.Success)
            {
                ok++;
                await ApplyRetentionAsync(task.Id, volume, opt, ct);
            }
            else
            {
                failures.Add($"{volume}: {result.Message}");
            }
        }

        var summary = $"{ok}/{volumes.Count} volumes backed up";
        if (failures.Count > 0)
        {
            summary += " – " + string.Join("; ", failures.Take(3));
        }
        return (failures.Count == 0, summary);
    }

    private async Task ApplyRetentionAsync(long taskId, string volume, ScheduleOptions opt, CancellationToken ct)
    {
        if (opt.RetentionCount <= 0 && opt.RetentionDays <= 0)
        {
            return;
        }

        var backups = await _db.VolumeBackups
            .Where(b => b.ScheduledTaskId == taskId && b.VolumeName == volume)
            .OrderByDescending(b => b.CreateDate)
            .ToListAsync(ct);

        var toDelete = new HashSet<long>();
        if (opt.RetentionCount > 0 && backups.Count > opt.RetentionCount)
        {
            foreach (var old in backups.Skip(opt.RetentionCount)) { toDelete.Add(old.Id); }
        }
        if (opt.RetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-opt.RetentionDays);
            foreach (var old in backups.Where(b => b.CreateDate < cutoff)) { toDelete.Add(old.Id); }
        }

        foreach (var id in toDelete)
        {
            await _backup.DeleteAsync(id, ct);
        }
    }
}

/// <summary>Runs a referenced SyncJob (GitOps) via the sync runner.</summary>
public sealed class SyncAction : IScheduleAction
{
    private readonly MatDockDbContext _db;
    private readonly SyncJobRunner _runner;

    public SyncAction(MatDockDbContext db, SyncJobRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    public ScheduleAction Type => ScheduleAction.Sync;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var opt = ScheduleOptions.Parse(task.OptionsJson);
        if (opt.SyncJobId is not { } id)
        {
            return (false, "No sync job referenced.");
        }

        var job = await _db.SyncJobs.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null)
        {
            return (false, "Referenced sync job not found.");
        }

        var summary = await _runner.RunAsync(job, force: false, ct);

        job.LastRunAt = DateTime.UtcNow;
        job.LastStatus = summary;
        await _db.SaveChangesAsync(ct);

        var ok = !summary.Contains("failed", StringComparison.OrdinalIgnoreCase)
                 && !summary.Contains("error", StringComparison.OrdinalIgnoreCase);
        return (ok, summary);
    }
}
