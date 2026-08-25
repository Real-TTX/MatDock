using MatDock.Core.Backups;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

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
    private readonly ISshClientFactory _sshClientFactory;
    private readonly EnvironmentService _environmentService;
    private readonly IBackupStorageFactory _storageFactory;
    private readonly MatDockOptions _options;
    private readonly ILogger<VolumeBackupService> _logger;

    public VolumeBackupService(
        MatDockDbContext db,
        ISshClientFactory sshClientFactory,
        EnvironmentService environmentService,
        IBackupStorageFactory storageFactory,
        IOptions<MatDockOptions> options,
        ILogger<VolumeBackupService> logger)
    {
        _db = db;
        _sshClientFactory = sshClientFactory;
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

    public async Task<BackupResult> BackupAsync(DockerEnvironment environment, string volumeName, BackupTarget? target, long? scheduleId = null, CancellationToken ct = default)
    {
        if (!VolumeCommands.IsValidVolumeName(volumeName))
        {
            return BackupResult.Fail($"Ungültiger Volume-Name: '{volumeName}'.");
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

            using var client = _sshClientFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            // Don't rely on tar's exit code to detect a missing volume: `docker run -v name:/x` would
            // silently CREATE an empty volume and back it up. Verify it exists first.
            var inspect = RunCommand(client, VolumeCommands.Inspect(volumeName, head));
            if (inspect.ExitStatus != 0)
            {
                return BackupResult.Fail($"Volume '{volumeName}' existiert auf dem Host nicht.");
            }

            using var exportCmd = client.CreateCommand(VolumeCommands.Export(volumeName, _options.HelperImage, head));
            exportCmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
            var async = exportCmd.BeginExecute();

            long bytes;
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
                return BackupResult.Fail($"Backup fehlgeschlagen: {FirstLine(exportCmd.Error)}");
            }

            var backup = new VolumeBackup
            {
                SourceEnvironmentId = environment.Id,
                SourceEnvironmentName = environment.Name,
                VolumeName = volumeName,
                FileName = fileName,
                SizeBytes = bytes,
                BackupTargetId = target?.Id,
                BackupTargetName = target?.Name ?? "Lokal",
                BackupScheduleId = scheduleId
            };
            _db.VolumeBackups.Add(backup);
            await _db.SaveChangesAsync(ct);

            return BackupResult.Ok(bytes, backup.Id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TryDeleteAsync(storage, fileName);
            return BackupResult.Fail("Zeitüberschreitung beim Backup.");
        }
        catch (Exception ex)
        {
            await TryDeleteAsync(storage, fileName);
            _logger.LogInformation(ex, "Backup of {Volume} on env {Env} failed.", volumeName, environment.Id);
            return BackupResult.Fail($"Fehler: {Innermost(ex).Message}");
        }
    }

    public async Task<RestoreResult> RestoreAsync(long backupId, DockerEnvironment target, string targetVolume, bool overwrite, CancellationToken ct = default)
    {
        if (!VolumeCommands.IsValidVolumeName(targetVolume))
        {
            return RestoreResult.Fail($"Ungültiger Ziel-Volume-Name: '{targetVolume}'.");
        }

        var backup = await GetAsync(backupId, ct);
        if (backup is null)
        {
            return RestoreResult.Fail("Backup nicht gefunden.");
        }

        // Resolve the archive's target even if it was soft-deleted; fail loudly rather than silently
        // falling back to local storage (which would look in the wrong place).
        BackupTarget? storageTarget = null;
        if (backup.BackupTargetId is { } stid)
        {
            storageTarget = await _db.BackupTargets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == stid, ct);
            if (storageTarget is null)
            {
                return RestoreResult.Fail("Das Backup-Ziel dieses Backups wurde gelöscht – Restore nicht möglich.");
            }
        }
        var storage = _storageFactory.Create(storageTarget);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));

        try
        {
            var settings = _environmentService.BuildSettings(target);
            var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

            using var client = _sshClientFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            // Open (and thereby verify) the archive BEFORE any destructive step, so a source failure
            // aborts the restore before the target volume is wiped.
            await using var source = await storage.OpenReadAsync(backup.FileName, cts.Token);

            var create = RunCommand(client, VolumeCommands.Create(targetVolume, head));
            if (create.ExitStatus != 0)
            {
                return RestoreResult.Fail($"Ziel-Volume konnte nicht angelegt werden: {FirstLine(create.StdErr)}");
            }

            if (!overwrite)
            {
                // Fail closed: if emptiness cannot be positively verified, do not risk overwriting data.
                var count = RunCommand(client, VolumeCommands.CountEntries(targetVolume, _options.HelperImage, head));
                if (count.ExitStatus != 0 || !int.TryParse(count.StdOut.Trim(), out var entries))
                {
                    return RestoreResult.Fail($"Ziel-Volume '{targetVolume}' konnte nicht geprüft werden – Restore abgebrochen: {FirstLine(count.StdErr)}");
                }
                if (entries > 0)
                {
                    return RestoreResult.Fail($"Ziel-Volume '{targetVolume}' enthält bereits Daten. Zum Überschreiben „Überschreiben“ aktivieren.");
                }
            }

            // On overwrite, wipe the target first so the restored state matches the archive exactly.
            using var importCmd = client.CreateCommand(VolumeCommands.Import(targetVolume, _options.HelperImage, head, clearFirst: overwrite));
            importCmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
            var input = importCmd.CreateInputStream();
            var async = importCmd.BeginExecute();

            var bytes = await StreamPump.CopyAsync(source, input, cts.Token);

            input.Close();
            importCmd.EndExecute(async);

            // Require an explicit success; null exit status = command killed / channel died = failure.
            if (importCmd.ExitStatus != 0)
            {
                return RestoreResult.Fail($"Restore fehlgeschlagen: {FirstLine(importCmd.Error)}");
            }

            return RestoreResult.Ok(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RestoreResult.Fail("Zeitüberschreitung beim Restore.");
        }
        catch (FileNotFoundException)
        {
            return RestoreResult.Fail("Die Backup-Datei existiert am Ziel nicht mehr.");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Restore of backup {Id} failed.", backupId);
            return RestoreResult.Fail($"Fehler: {Innermost(ex).Message}");
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

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(SshClient client, string command)
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
            return "unbekannter Fehler";
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "unbekannter Fehler";
    }
}
