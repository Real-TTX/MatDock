using System.Diagnostics;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Volumes;

/// <summary>
/// Moves a Docker volume from a source host to a target host by streaming a tar archive over SSH:
/// on the source <c>docker run … tar -cf -</c> writes to stdout, and MatDock pipes those bytes into
/// <c>docker run -i … tar -xf -</c> on the target — no temporary files, no intermediate storage.
/// </summary>
public sealed class VolumeMigrationService
{
    private readonly IHostSessionFactory _hostSessionFactory;
    private readonly VolumeBackupService _backupService;
    private readonly MatDockOptions _options;
    private readonly ILogger<VolumeMigrationService> _logger;

    public VolumeMigrationService(
        IHostSessionFactory hostSessionFactory,
        VolumeBackupService backupService,
        IOptions<MatDockOptions> options,
        ILogger<VolumeMigrationService> logger)
    {
        _hostSessionFactory = hostSessionFactory;
        _backupService = backupService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VolumeMigrationResult> MigrateAsync(VolumeMigrationRequest request, CancellationToken cancellationToken = default)
    {
        var image = _options.HelperImage;
        var steps = new List<string>();

        if (!VolumeCommands.IsValidVolumeName(request.SourceVolume) || !VolumeCommands.IsValidVolumeName(request.TargetVolume))
        {
            return VolumeMigrationResult.Fail("Invalid volume name.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sourceHead = VolumeCommands.DockerHead(request.Source.UseSudo, request.Source.DockerHost);
            var targetHead = VolumeCommands.DockerHead(request.Target.UseSudo, request.Target.DockerHost);

            using var source = _hostSessionFactory.Create(request.Source);
            using var target = _hostSessionFactory.Create(request.Target);
            await source.ConnectAsync(cts.Token);
            await target.ConnectAsync(cts.Token);
            steps.Add("Connected to source and target.");

            // 0) Verify the source volume exists (docker would otherwise auto-create an empty one).
            var inspect = RunCommand(source, VolumeCommands.Inspect(request.SourceVolume, sourceHead));
            if (inspect.ExitStatus != 0)
            {
                return VolumeMigrationResult.Fail($"Source volume '{request.SourceVolume}' does not exist.", steps);
            }

            // 1) Ensure the target volume exists.
            var create = RunCommand(target, VolumeCommands.Create(request.TargetVolume, targetHead));
            if (create.ExitStatus != 0)
            {
                return VolumeMigrationResult.Fail($"Target volume could not be created: {FirstLine(create.StdErr)}", steps);
            }
            steps.Add($"Target volume '{request.TargetVolume}' ready.");

            // 2) Refuse to clobber a non-empty target unless explicitly allowed (fail closed if unsure).
            if (!request.Overwrite)
            {
                var count = RunCommand(target, VolumeCommands.CountEntries(request.TargetVolume, image, targetHead));
                if (count.ExitStatus != 0 || !int.TryParse(count.StdOut.Trim(), out var entries))
                {
                    return VolumeMigrationResult.Fail(
                        $"Target volume '{request.TargetVolume}' could not be checked - migration aborted: {FirstLine(count.StdErr)}", steps);
                }
                if (entries > 0)
                {
                    return VolumeMigrationResult.Fail(
                        $"Target volume '{request.TargetVolume}' already contains data. Enable the \"Overwrite\" option to overwrite it.", steps);
                }
            }

            // 3) Optionally quiesce the source volume's containers so the archive is consistent.
            var quiesce = request.StopContainers
                ? Containers.ContainerQuiesce.StopRunning(source, sourceHead, request.SourceVolume, _options.SshTimeoutSeconds)
                : Containers.QuiesceResult.None;
            if (quiesce.StoppedIds.Count > 0)
            {
                steps.Add($"{quiesce.StoppedIds.Count} container(s) stopped on the source.");
            }
            if (quiesce.Warning is not null)
            {
                steps.Add($"⚠ {quiesce.Warning}");
            }

            long bytes = 0;
            try
            {
                // Stream source -> target. On overwrite, wipe the target first so it matches the source.
                var timeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
                using var exportCmd = source.CreateCommand(VolumeCommands.Export(request.SourceVolume, image, sourceHead));
                using var importCmd = target.CreateCommand(VolumeCommands.Import(request.TargetVolume, image, targetHead, clearFirst: request.Overwrite));
                exportCmd.CommandTimeout = timeout;
                importCmd.CommandTimeout = timeout;
                exportCmd.BufferOutput = false; // pipe the tar stdout live (local); never buffer the whole archive

                // SSH.NET 2026: CreateInputStream() requires the channel to be OPEN, and BeginExecute()
                // opens the channel synchronously before it returns — so the input stream must be created
                // AFTER BeginExecute(), never before (otherwise: "input stream can be used only during
                // execution"). OutputStream is likewise (re)created by BeginExecute for this run.
                var exportAsync = exportCmd.BeginExecute();
                var importAsync = importCmd.BeginExecute();
                var importInput = importCmd.CreateInputStream();

                bytes = await StreamPump.CopyAsync(exportCmd.OutputStream, importInput, cts.Token);
                importInput.Close(); // signal EOF so the remote tar finishes

                exportCmd.EndExecute(exportAsync);
                importCmd.EndExecute(importAsync);

                // Require explicit success; null exit status = command killed / channel died = failure.
                if (exportCmd.ExitStatus != 0)
                {
                    return VolumeMigrationResult.Fail($"Export from source failed: {FirstLine(exportCmd.Error)}", steps);
                }
                if (importCmd.ExitStatus != 0)
                {
                    return VolumeMigrationResult.Fail($"Import into target failed: {FirstLine(importCmd.Error)}", steps);
                }
            }
            finally
            {
                // Always restart the source containers we stopped, even on failure.
                Containers.ContainerQuiesce.Start(source, sourceHead, quiesce.StoppedIds, _options.SshTimeoutSeconds);
            }

            steps.Add($"{VolumeMigrationResult.FormatBytes(bytes)} transferred.");
            stopwatch.Stop();
            return VolumeMigrationResult.Ok(bytes, stopwatch.Elapsed.TotalSeconds, steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return VolumeMigrationResult.Fail("Migration timed out.", steps);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Volume migration {Src} -> {Dst} failed.", request.SourceVolume, request.TargetVolume);
            return VolumeMigrationResult.Fail($"Error: {Innermost(ex).Message}", steps);
        }
    }

    /// <summary>
    /// Migrates by backing the source volume up to a backup target and then restoring that backup into
    /// the target host — reusing the tested backup/restore pipeline. The intermediate backup is a durable
    /// safety net: it is kept on any failure, and on success only removed when <paramref name="keepBackup"/>
    /// is false.
    /// </summary>
    public async Task<VolumeMigrationResult> MigrateViaBackupAsync(
        DockerEnvironment source,
        string sourceVolume,
        DockerEnvironment target,
        string targetVolume,
        bool overwrite,
        BackupTarget? backupTarget,
        bool keepBackup,
        bool stopContainers = false,
        CancellationToken cancellationToken = default)
    {
        if (!VolumeCommands.IsValidVolumeName(sourceVolume) || !VolumeCommands.IsValidVolumeName(targetVolume))
        {
            return VolumeMigrationResult.Fail("Invalid volume name.");
        }

        var steps = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        // One wall-clock budget for the whole two-phase operation (like the Direct path), instead of a
        // fresh full timeout per sub-call.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));

        try
        {
            // 1) Back the source up (safety net + transport artifact).
            var backup = await _backupService.BackupAsync(source, sourceVolume, backupTarget, scheduleId: null, ct: cts.Token, stopContainers: stopContainers);
            if (!backup.Success || backup.BackupId is not { } backupId)
            {
                return VolumeMigrationResult.Fail($"Backup of the source failed: {backup.Message}", steps);
            }
            steps.Add($"Backup created ({VolumeMigrationResult.FormatBytes(backup.BytesTransferred)}) on \"{backupTarget?.Name ?? "Local"}\".");

            // 2) Restore that backup into the target. On failure the backup is intentionally KEPT so the
            //    restore can be retried without touching the source again.
            var restore = await _backupService.RestoreAsync(backupId, target, targetVolume, overwrite, ct: cts.Token, stopContainers: stopContainers);
            if (!restore.Success)
            {
                steps.Add("Backup is kept - the restore can be retried from it.");
                return VolumeMigrationResult.Fail($"Restore into target failed: {restore.Message}", steps);
            }
            steps.Add($"Restore into target \"{target.Name}/{targetVolume}\" successful.");

            // 3) The data is already at the target now — a cleanup failure must NOT turn a completed
            //    migration into a reported failure. Keep by default; on delete, downgrade errors to a note.
            if (keepBackup)
            {
                steps.Add("Intermediate backup kept as a safety copy.");
            }
            else
            {
                try
                {
                    var deleted = await _backupService.DeleteAsync(backupId, cts.Token);
                    steps.Add(deleted ? "Intermediate backup removed." : "Intermediate backup could not be removed (it is kept).");
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "Cleanup of intermediate backup {Id} after migration failed.", backupId);
                    steps.Add("Intermediate backup could not be removed (it is kept).");
                }
            }

            stopwatch.Stop();
            return VolumeMigrationResult.Ok(restore.BytesTransferred, stopwatch.Elapsed.TotalSeconds, steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return VolumeMigrationResult.Fail("Migration timed out.", steps);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Backup-based migration {Src} -> {Dst} failed.", sourceVolume, targetVolume);
            return VolumeMigrationResult.Fail($"Error: {Innermost(ex).Message}", steps);
        }
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(IHostSession client, string commandText)
    {
        using var command = client.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds));
        var stdout = command.Execute();
        return (command.ExitStatus ?? -1, stdout ?? string.Empty, command.Error ?? string.Empty);
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
