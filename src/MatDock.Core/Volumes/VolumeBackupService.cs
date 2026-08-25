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
    private readonly AppPaths _paths;
    private readonly MatDockOptions _options;
    private readonly ILogger<VolumeBackupService> _logger;

    public VolumeBackupService(
        MatDockDbContext db,
        ISshClientFactory sshClientFactory,
        EnvironmentService environmentService,
        AppPaths paths,
        IOptions<MatDockOptions> options,
        ILogger<VolumeBackupService> logger)
    {
        _db = db;
        _sshClientFactory = sshClientFactory;
        _environmentService = environmentService;
        _paths = paths;
        _options = options.Value;
        _logger = logger;
    }

    public Task<List<VolumeBackup>> GetAllAsync(CancellationToken ct = default)
        => _db.VolumeBackups.AsNoTracking().OrderByDescending(b => b.CreateDate).ToListAsync(ct);

    public Task<VolumeBackup?> GetAsync(long id, CancellationToken ct = default)
        => _db.VolumeBackups.FirstOrDefaultAsync(b => b.Id == id, ct);

    public static string BuildFileName(long environmentId, string volumeName, DateTime timestampUtc)
        => $"{environmentId}_{volumeName}_{timestampUtc:yyyyMMddHHmmss}.tar";

    public async Task<BackupResult> BackupAsync(DockerEnvironment environment, string volumeName, CancellationToken ct = default)
    {
        if (!VolumeCommands.IsValidVolumeName(volumeName))
        {
            return BackupResult.Fail($"Ungültiger Volume-Name: '{volumeName}'.");
        }

        _paths.EnsureCreated();
        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var fileName = BuildFileName(environment.Id, volumeName, DateTime.UtcNow);
        var fullPath = Path.Combine(_paths.BackupsPath, fileName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));

        try
        {
            using var client = _sshClientFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            using var exportCmd = client.CreateCommand(VolumeCommands.Export(volumeName, _options.HelperImage, head));
            exportCmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
            var async = exportCmd.BeginExecute();

            long bytes;
            await using (var file = File.Create(fullPath))
            {
                bytes = await StreamPump.CopyAsync(exportCmd.OutputStream, file, cts.Token);
            }

            exportCmd.EndExecute(async);
            if (exportCmd.ExitStatus is not (null or 0))
            {
                TryDelete(fullPath);
                return BackupResult.Fail($"Backup fehlgeschlagen: {FirstLine(exportCmd.Error)}");
            }

            var backup = new VolumeBackup
            {
                SourceEnvironmentId = environment.Id,
                SourceEnvironmentName = environment.Name,
                VolumeName = volumeName,
                FileName = fileName,
                SizeBytes = bytes
            };
            _db.VolumeBackups.Add(backup);
            await _db.SaveChangesAsync(ct);

            return BackupResult.Ok(bytes, backup.Id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(fullPath);
            return BackupResult.Fail("Zeitüberschreitung beim Backup.");
        }
        catch (Exception ex)
        {
            TryDelete(fullPath);
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

        var fullPath = Path.Combine(_paths.BackupsPath, backup.FileName);
        if (!File.Exists(fullPath))
        {
            return RestoreResult.Fail("Die Backup-Datei existiert nicht mehr.");
        }

        var settings = _environmentService.BuildSettings(target);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));

        try
        {
            using var client = _sshClientFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            var create = RunCommand(client, VolumeCommands.Create(targetVolume, head));
            if (create.ExitStatus != 0)
            {
                return RestoreResult.Fail($"Ziel-Volume konnte nicht angelegt werden: {FirstLine(create.StdErr)}");
            }

            if (!overwrite)
            {
                var count = RunCommand(client, VolumeCommands.CountEntries(targetVolume, _options.HelperImage, head));
                if (count.ExitStatus == 0 && int.TryParse(count.StdOut.Trim(), out var entries) && entries > 0)
                {
                    return RestoreResult.Fail($"Ziel-Volume '{targetVolume}' enthält bereits Daten. Zum Überschreiben „Überschreiben“ aktivieren.");
                }
            }

            using var importCmd = client.CreateCommand(VolumeCommands.Import(targetVolume, _options.HelperImage, head));
            importCmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
            var input = importCmd.CreateInputStream();
            var async = importCmd.BeginExecute();

            long bytes;
            await using (var file = File.OpenRead(fullPath))
            {
                bytes = await StreamPump.CopyAsync(file, input, cts.Token);
            }

            input.Close();
            importCmd.EndExecute(async);

            if (importCmd.ExitStatus is not (null or 0))
            {
                return RestoreResult.Fail($"Restore fehlgeschlagen: {FirstLine(importCmd.Error)}");
            }

            return RestoreResult.Ok(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RestoreResult.Fail("Zeitüberschreitung beim Restore.");
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

        TryDelete(Path.Combine(_paths.BackupsPath, backup.FileName));
        _db.VolumeBackups.Remove(backup); // soft delete of the row
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(SshClient client, string command)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds));
        var stdout = cmd.Execute();
        return (cmd.ExitStatus ?? -1, stdout ?? string.Empty, cmd.Error ?? string.Empty);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* best effort */ }
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
