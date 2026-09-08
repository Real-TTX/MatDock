using MatDock.Core.Backups;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Volumes;

/// <summary>
/// On-demand backup and restore of Docker volumes. A backup streams the volume's tar archive from the
/// remote host into a file in the MatDock data volume (<c>/data/backups</c>); a restore streams that
/// file back into a volume on a (possibly different) host. Uses the same tar-over-SSH mechanism as
/// migration, so no temporary files are created on the remote side.
/// </summary>
public sealed class VolumeBackupService
{
    private readonly MatDockDbContext _db;
    private readonly IHostSessionFactory _hostSessionFactory;
    private readonly EnvironmentService _environmentService;
    private readonly IBackupStorageFactory _storageFactory;
    private readonly MatDockOptions _options;
    private readonly ILogger<VolumeBackupService> _logger;

    public VolumeBackupService(
        MatDockDbContext db,
        IHostSessionFactory hostSessionFactory,
        EnvironmentService environmentService,
        IBackupStorageFactory storageFactory,
        IOptions<MatDockOptions> options,
        ILogger<VolumeBackupService> logger)
    {
        _db = db;
        _hostSessionFactory = hostSessionFactory;
        _environmentService = environmentService;
        _storageFactory = storageFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<List<VolumeBackup>> GetAllAsync(CancellationToken ct = default)
        => _db.VolumeBackups.AsNoTracking().OrderByDescending(b => b.CreateDate).ToListAsync(ct);

    public Task<VolumeBackup?> GetAsync(long id, CancellationToken ct = default)
        => _db.VolumeBackups.FirstOrDefaultAsync(b => b.Id == id, ct);

    public static string BuildFileName(long environmentId, string volumeName, DateTime timestampUtc, string? suffix = null)
        => suffix is null
            ? $"{environmentId}_{volumeName}_{timestampUtc:yyyyMMddHHmmss}.tar"
            : $"{environmentId}_{volumeName}_{timestampUtc:yyyyMMddHHmmss}_{suffix}.tar";

    /// <summary>
    /// Parses a MatDock volume-archive file name <c>{envId}_{volume}_{yyyyMMddHHmmss}[_{suffix}].tar</c>
    /// back into its parts. The volume name itself may contain underscores. Returns false if it doesn't
    /// match the MatDock convention (e.g. a truly foreign archive).
    /// </summary>
    public static bool TryParseArchiveName(string fileName, out long environmentId, out string volumeName, out DateTime timestampUtc)
    {
        environmentId = 0;
        volumeName = string.Empty;
        timestampUtc = default;

        var name = Path.GetFileName(fileName);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var parts = name.Split('_');
        if (parts.Length < 3 || !long.TryParse(parts[0], out environmentId))
        {
            return false;
        }

        // The timestamp is the last 14-digit segment (an optional 8-char suffix may follow it).
        var tsIdx = -1;
        for (var i = parts.Length - 1; i >= 2; i--)
        {
            if (parts[i].Length == 14 && parts[i].All(char.IsDigit))
            {
                tsIdx = i;
                break;
            }
        }

        if (tsIdx < 2 || !DateTime.TryParseExact(parts[tsIdx], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out timestampUtc))
        {
            return false;
        }

        volumeName = string.Join('_', parts[1..tsIdx]);
        return volumeName.Length > 0;
    }

    public async Task<BackupResult> BackupAsync(DockerEnvironment environment, string volumeName, BackupTarget? target, long? scheduleId = null, CancellationToken ct = default, bool stopContainers = false, long? scheduledTaskId = null)
    {
        if (!VolumeCommands.IsValidVolumeName(volumeName))
        {
            return BackupResult.Fail($"Invalid volume name: '{volumeName}'.");
        }

        // Unique even within the same second (avoids overwriting a concurrent backup's file).
        var fileName = BuildFileName(environment.Id, volumeName, DateTime.UtcNow, Guid.NewGuid().ToString("N")[..8]);
        var storage = _storageFactory.Create(target);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));

        try
        {
            var settings = _environmentService.BuildSettings(environment);
            var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

            using var client = _hostSessionFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            // Don't rely on tar's exit code to detect a missing volume: `docker run -v name:/x` would
            // silently CREATE an empty volume and back it up. Verify it exists first.
            var inspect = RunCommand(client, VolumeCommands.Inspect(volumeName, head));
            if (inspect.ExitStatus != 0)
            {
                return BackupResult.Fail($"Volume '{volumeName}' does not exist on the host.");
            }

            // Optionally quiesce the volume's containers so the archive is a consistent snapshot.
            var quiesce = stopContainers
                ? Containers.ContainerQuiesce.StopRunning(client, head, volumeName, _options.SshTimeoutSeconds)
                : Containers.QuiesceResult.None;

            long bytes = 0;
            try
            {
                using var exportCmd = client.CreateCommand(VolumeCommands.Export(volumeName, _options.HelperImage, head));
                exportCmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
                exportCmd.BufferOutput = false; // stream the tar stdout live instead of buffering it in memory
                var async = exportCmd.BeginExecute();

                await using (var destination = await storage.OpenWriteAsync(fileName, cts.Token))
                {
                    bytes = await StreamPump.CopyAsync(exportCmd.OutputStream, destination, cts.Token);
                }

                exportCmd.EndExecute(async);
                // Require an explicit success: a null exit status means the command was killed / the channel
                // died mid-transfer, which would otherwise persist a truncated archive as a valid backup.
                if (exportCmd.ExitStatus != 0)
                {
                    await TryDeleteAsync(storage, fileName);
                    return BackupResult.Fail($"Backup failed: {FirstLine(exportCmd.Error)}");
                }
            }
            finally
            {
                // Always restart whatever we stopped, even if the export failed.
                Containers.ContainerQuiesce.Start(client, head, quiesce.StoppedIds, _options.SshTimeoutSeconds);
            }

            var backup = new VolumeBackup
            {
                SourceEnvironmentId = environment.Id,
                SourceEnvironmentName = environment.Name,
                VolumeName = volumeName,
                FileName = fileName,
                SizeBytes = bytes,
                BackupTargetId = target?.Id,
                BackupTargetName = target?.Name ?? "Local",
                BackupScheduleId = scheduleId,
                ScheduledTaskId = scheduledTaskId
            };
            _db.VolumeBackups.Add(backup);
            await _db.SaveChangesAsync(ct);

            return BackupResult.Ok(bytes, backup.Id, quiesce.Warning);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TryDeleteAsync(storage, fileName);
            return BackupResult.Fail("Backup timed out.");
        }
        catch (Exception ex)
        {
            await TryDeleteAsync(storage, fileName);
            _logger.LogInformation(ex, "Backup of {Volume} on env {Env} failed.", volumeName, environment.Id);
            return BackupResult.Fail($"Error: {Innermost(ex).Message}");
        }
    }

    public async Task<RestoreResult> RestoreAsync(long backupId, DockerEnvironment target, string targetVolume, bool overwrite, CancellationToken ct = default, bool stopContainers = false)
    {
        var backup = await GetAsync(backupId, ct);
        if (backup is null)
        {
            return RestoreResult.Fail("Backup not found.");
        }

        // Resolve the archive's target even if it was soft-deleted; fail loudly rather than silently
        // falling back to local storage (which would look in the wrong place).
        BackupTarget? storageTarget = null;
        if (backup.BackupTargetId is { } stid)
        {
            storageTarget = await _db.BackupTargets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == stid, ct);
            if (storageTarget is null)
            {
                return RestoreResult.Fail("The backup target of this backup has been deleted - restore is not possible.");
            }
        }

        return await RestoreCoreAsync(_storageFactory.Create(storageTarget), backup.FileName, target, targetVolume, overwrite, stopContainers, ct);
    }

    /// <summary>
    /// Restores an archive that is present at a target by FILE NAME (not a DB record) — used to restore
    /// backups created by an older/other MatDock instance that wrote to the same target.
    /// </summary>
    public Task<RestoreResult> RestoreFromFileAsync(BackupTarget? storageTarget, string fileName, DockerEnvironment target, string targetVolume, bool overwrite, bool stopContainers = false, CancellationToken ct = default)
        => RestoreCoreAsync(_storageFactory.Create(storageTarget), Path.GetFileName(fileName), target, targetVolume, overwrite, stopContainers, ct);

    private async Task<RestoreResult> RestoreCoreAsync(IBackupStorage storage, string fileName, DockerEnvironment target, string targetVolume, bool overwrite, bool stopContainers, CancellationToken ct)
    {
        if (!VolumeCommands.IsValidVolumeName(targetVolume))
        {
            return RestoreResult.Fail($"Invalid target volume name: '{targetVolume}'.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));

        try
        {
            var settings = _environmentService.BuildSettings(target);
            var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

            using var client = _hostSessionFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            // Open (and thereby verify) the archive BEFORE any destructive step, so a source failure
            // aborts the restore before the target volume is wiped.
            await using var source = await storage.OpenReadAsync(fileName, cts.Token);

            var create = RunCommand(client, VolumeCommands.Create(targetVolume, head));
            if (create.ExitStatus != 0)
            {
                return RestoreResult.Fail($"Target volume could not be created: {FirstLine(create.StdErr)}");
            }

            if (!overwrite)
            {
                // Fail closed: if emptiness cannot be positively verified, do not risk overwriting data.
                var count = RunCommand(client, VolumeCommands.CountEntries(targetVolume, _options.HelperImage, head));
                if (count.ExitStatus != 0 || !int.TryParse(count.StdOut.Trim(), out var entries))
                {
                    return RestoreResult.Fail($"Target volume '{targetVolume}' could not be checked - restore aborted: {FirstLine(count.StdErr)}");
                }
                if (entries > 0)
                {
                    return RestoreResult.Fail($"Target volume '{targetVolume}' already contains data. Enable \"Overwrite\" to overwrite it.");
                }
            }

            // Optionally stop the target volume's containers so the wipe/extract is not fought by writers.
            var quiesce = stopContainers
                ? Containers.ContainerQuiesce.StopRunning(client, head, targetVolume, _options.SshTimeoutSeconds)
                : Containers.QuiesceResult.None;

            long bytes = 0;
            try
            {
                // On overwrite, wipe the target first so the restored state matches the archive exactly.
                using var importCmd = client.CreateCommand(VolumeCommands.Import(targetVolume, _options.HelperImage, head, clearFirst: overwrite));
                importCmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
                // SSH.NET 2026: CreateInputStream() requires an open channel, which BeginExecute() opens
                // synchronously — so it must be called AFTER BeginExecute(), never before.
                var async = importCmd.BeginExecute();
                var input = importCmd.CreateInputStream();

                bytes = await StreamPump.CopyAsync(source, input, cts.Token);

                input.Close();
                importCmd.EndExecute(async);

                // Require an explicit success; null exit status = command killed / channel died = failure.
                if (importCmd.ExitStatus != 0)
                {
                    return RestoreResult.Fail($"Restore failed: {FirstLine(importCmd.Error)}");
                }
            }
            finally
            {
                Containers.ContainerQuiesce.Start(client, head, quiesce.StoppedIds, _options.SshTimeoutSeconds);
            }

            return RestoreResult.Ok(bytes, quiesce.Warning);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RestoreResult.Fail("Restore timed out.");
        }
        catch (FileNotFoundException)
        {
            return RestoreResult.Fail("The backup file no longer exists at the target.");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Restore of '{File}' failed.", fileName);
            return RestoreResult.Fail($"Error: {Innermost(ex).Message}");
        }
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var backup = await _db.VolumeBackups.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (backup is null)
        {
            return false;
        }

        var storageTarget = backup.BackupTargetId is { } stid
            ? await _db.BackupTargets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == stid, ct)
            : null;
        var fileName = backup.FileName;

        // Commit the row removal first; only delete the file once the DB change succeeded.
        _db.VolumeBackups.Remove(backup); // soft delete of the row
        await _db.SaveChangesAsync(ct);
        await TryDeleteAsync(_storageFactory.Create(storageTarget), fileName);
        return true;
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(IHostSession client, string command)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds));
        var stdout = cmd.Execute();
        return (cmd.ExitStatus ?? -1, stdout ?? string.Empty, cmd.Error ?? string.Empty);
    }

    private static async Task TryDeleteAsync(Backups.IBackupStorage storage, string fileName)
    {
        try { await storage.DeleteAsync(fileName); } catch { /* best effort cleanup */ }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown error";
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "unknown error";
    }
}
