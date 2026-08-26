using System.Diagnostics;
using MatDock.Core.Configuration;
using MatDock.Core.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MatDock.Core.Volumes;

/// <summary>
/// Moves a Docker volume from a source host to a target host by streaming a tar archive over SSH:
/// on the source <c>docker run … tar -cf -</c> writes to stdout, and MatDock pipes those bytes into
/// <c>docker run -i … tar -xf -</c> on the target — no temporary files, no intermediate storage.
/// </summary>
public sealed class VolumeMigrationService
{
    private readonly ISshClientFactory _sshClientFactory;
    private readonly MatDockOptions _options;
    private readonly ILogger<VolumeMigrationService> _logger;

    public VolumeMigrationService(
        ISshClientFactory sshClientFactory,
        IOptions<MatDockOptions> options,
        ILogger<VolumeMigrationService> logger)
    {
        _sshClientFactory = sshClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VolumeMigrationResult> MigrateAsync(VolumeMigrationRequest request, CancellationToken cancellationToken = default)
    {
        var image = _options.HelperImage;
        var steps = new List<string>();

        if (!VolumeCommands.IsValidVolumeName(request.SourceVolume) || !VolumeCommands.IsValidVolumeName(request.TargetVolume))
        {
            return VolumeMigrationResult.Fail("Ungültiger Volume-Name.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds)));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sourceHead = VolumeCommands.DockerHead(request.Source.UseSudo, request.Source.DockerHost);
            var targetHead = VolumeCommands.DockerHead(request.Target.UseSudo, request.Target.DockerHost);

            using var source = _sshClientFactory.Create(request.Source);
            using var target = _sshClientFactory.Create(request.Target);
            await source.ConnectAsync(cts.Token);
            await target.ConnectAsync(cts.Token);
            steps.Add("Mit Quelle und Ziel verbunden.");

            // 0) Verify the source volume exists (docker would otherwise auto-create an empty one).
            var inspect = RunCommand(source, VolumeCommands.Inspect(request.SourceVolume, sourceHead));
            if (inspect.ExitStatus != 0)
            {
                return VolumeMigrationResult.Fail($"Quell-Volume '{request.SourceVolume}' existiert nicht.", steps);
            }

            // 1) Ensure the target volume exists.
            var create = RunCommand(target, VolumeCommands.Create(request.TargetVolume, targetHead));
            if (create.ExitStatus != 0)
            {
                return VolumeMigrationResult.Fail($"Ziel-Volume konnte nicht angelegt werden: {FirstLine(create.StdErr)}", steps);
            }
            steps.Add($"Ziel-Volume '{request.TargetVolume}' bereit.");

            // 2) Refuse to clobber a non-empty target unless explicitly allowed (fail closed if unsure).
            if (!request.Overwrite)
            {
                var count = RunCommand(target, VolumeCommands.CountEntries(request.TargetVolume, image, targetHead));
                if (count.ExitStatus != 0 || !int.TryParse(count.StdOut.Trim(), out var entries))
                {
                    return VolumeMigrationResult.Fail(
                        $"Ziel-Volume '{request.TargetVolume}' konnte nicht geprüft werden – Migration abgebrochen: {FirstLine(count.StdErr)}", steps);
                }
                if (entries > 0)
                {
                    return VolumeMigrationResult.Fail(
                        $"Ziel-Volume '{request.TargetVolume}' enthält bereits Daten. Zum Überschreiben die Option „Überschreiben“ aktivieren.", steps);
                }
            }

            // 3) Stream source -> target. On overwrite, wipe the target first so it matches the source.
            var timeout = TimeSpan.FromSeconds(Math.Max(30, _options.MigrationTimeoutSeconds));
            using var exportCmd = source.CreateCommand(VolumeCommands.Export(request.SourceVolume, image, sourceHead));
            using var importCmd = target.CreateCommand(VolumeCommands.Import(request.TargetVolume, image, targetHead, clearFirst: request.Overwrite));
            exportCmd.CommandTimeout = timeout;
            importCmd.CommandTimeout = timeout;

            // SSH.NET 2026: CreateInputStream() requires the channel to be OPEN, and BeginExecute()
            // opens the channel synchronously before it returns — so the input stream must be created
            // AFTER BeginExecute(), never before (otherwise: "input stream can be used only during
            // execution"). OutputStream is likewise (re)created by BeginExecute for this run.
            var exportAsync = exportCmd.BeginExecute();
            var importAsync = importCmd.BeginExecute();
            var importInput = importCmd.CreateInputStream();

            long bytes = await StreamPump.CopyAsync(exportCmd.OutputStream, importInput, cts.Token);
            importInput.Close(); // signal EOF so the remote tar finishes

            exportCmd.EndExecute(exportAsync);
            importCmd.EndExecute(importAsync);

            // Require explicit success; null exit status = command killed / channel died = failure.
            if (exportCmd.ExitStatus != 0)
            {
                return VolumeMigrationResult.Fail($"Export der Quelle fehlgeschlagen: {FirstLine(exportCmd.Error)}", steps);
            }
            if (importCmd.ExitStatus != 0)
            {
                return VolumeMigrationResult.Fail($"Import ins Ziel fehlgeschlagen: {FirstLine(importCmd.Error)}", steps);
            }

            steps.Add($"{VolumeMigrationResult.FormatBytes(bytes)} übertragen.");
            stopwatch.Stop();
            return VolumeMigrationResult.Ok(bytes, stopwatch.Elapsed.TotalSeconds, steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return VolumeMigrationResult.Fail("Zeitüberschreitung bei der Migration.", steps);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Volume migration {Src} -> {Dst} failed.", request.SourceVolume, request.TargetVolume);
            return VolumeMigrationResult.Fail($"Fehler: {Innermost(ex).Message}", steps);
        }
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(SshClient client, string commandText)
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
