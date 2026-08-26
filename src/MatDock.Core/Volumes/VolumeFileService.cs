using System.Text;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MatDock.Core.Volumes;

public sealed record VolumeFileEntry(string Name, bool IsDirectory, long Size);

public sealed record FileReadResult(string Content, bool Truncated, bool IsBinary, bool NonUtf8)
{
    /// <summary>Safe to edit &amp; save (a full, valid-UTF-8, non-binary text file).</summary>
    public bool Editable => !Truncated && !IsBinary && !NonUtf8;
}

/// <summary>Browses and edits files inside a Docker volume via a throwaway busybox container over SSH.</summary>
public sealed class VolumeFileService
{
    private readonly ISshClientFactory _sshClientFactory;
    private readonly EnvironmentService _environmentService;
    private readonly MatDockOptions _options;
    private readonly ILogger<VolumeFileService> _logger;

    public VolumeFileService(ISshClientFactory sshClientFactory, EnvironmentService environmentService, IOptions<MatDockOptions> options, ILogger<VolumeFileService> logger)
    {
        _sshClientFactory = sshClientFactory;
        _environmentService = environmentService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<VolumeFileEntry> Entries, string? Error)> ListAsync(DockerEnvironment env, string volume, string? relPath, CancellationToken ct = default)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return (Array.Empty<VolumeFileEntry>(), "Ungültiger Volume-Name.");
        }

        var rel = VolumeFileCommands.NormalizeRelPath(relPath);
        if (rel is null)
        {
            return (Array.Empty<VolumeFileEntry>(), "Ungültiger Pfad.");
        }

        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                var result = await RunAsync(client, VolumeFileCommands.List(head, _options.HelperImage, volume, rel), ct);
                if (result.ExitStatus != 0)
                {
                    return (Array.Empty<VolumeFileEntry>(), "Verzeichnis konnte nicht gelesen werden.");
                }

                return (ParseListing(result.StdOut), null);
            }
        }
        catch (Exception ex)
        {
            return (Array.Empty<VolumeFileEntry>(), Describe(ex));
        }
    }

    public async Task<(FileReadResult? File, string? Error)> ReadAsync(DockerEnvironment env, string volume, string relPath, CancellationToken ct = default)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return (null, "Ungültiger Volume-Name.");
        }

        var rel = VolumeFileCommands.NormalizeRelPath(relPath);
        if (string.IsNullOrEmpty(rel))
        {
            return (null, "Keine Datei angegeben.");
        }

        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                // Read the RAW bytes (not the UTF-8-decoded string) so truncation is measured in bytes
                // and non-UTF-8/binary files are detected exactly — a decoded string would hide both.
                using var cmd = client.CreateCommand(VolumeFileCommands.Read(head, _options.HelperImage, volume, rel));
                var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.SshTimeoutSeconds));
                cmd.CommandTimeout = timeout;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                var async = cmd.BeginExecute();
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    await cmd.OutputStream.CopyToAsync(ms, cts.Token);
                    bytes = ms.ToArray();
                }
                cmd.EndExecute(async);

                if (cmd.ExitStatus != 0)
                {
                    return (null, "Datei konnte nicht gelesen werden (existiert sie und ist es eine reguläre Datei?).");
                }

                var truncated = bytes.Length > VolumeFileCommands.MaxReadBytes;
                var slice = truncated ? bytes[..VolumeFileCommands.MaxReadBytes] : bytes;
                var isBinary = Array.IndexOf(slice, (byte)0) >= 0;
                var nonUtf8 = !isBinary && !truncated && !IsStrictUtf8(slice);
                var content = isBinary ? string.Empty : Encoding.UTF8.GetString(slice);
                return (new FileReadResult(content, truncated, isBinary, nonUtf8), null);
            }
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    private static bool IsStrictUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public Task<(bool Ok, string Message)> WriteAsync(DockerEnvironment env, string volume, string relPath, string content, CancellationToken ct = default)
        => MutateWithStdinAsync(env, volume, relPath, content, ct);

    public Task<(bool Ok, string Message)> MakeDirAsync(DockerEnvironment env, string volume, string relPath, CancellationToken ct = default)
        => MutateAsync(env, volume, relPath, VolumeFileCommands.MakeDir, "Ordner erstellt.", ct);

    public Task<(bool Ok, string Message)> CreateFileAsync(DockerEnvironment env, string volume, string relPath, CancellationToken ct = default)
        => MutateAsync(env, volume, relPath, VolumeFileCommands.CreateFile, "Datei erstellt.", ct);

    public async Task<(bool Ok, string Message)> DeleteAsync(DockerEnvironment env, string volume, string relPath, CancellationToken ct = default)
    {
        var rel = VolumeFileCommands.NormalizeRelPath(relPath);
        if (string.IsNullOrEmpty(rel))
        {
            return (false, "Das Wurzelverzeichnis kann nicht gelöscht werden.");
        }

        return await MutateAsync(env, volume, relPath, VolumeFileCommands.Delete, "Gelöscht.", ct);
    }

    public Task<(bool Ok, string Message)> MoveAsync(DockerEnvironment env, string volume, string srcRel, string dstRel, CancellationToken ct = default)
        => Mutate2Async(env, volume, srcRel, dstRel, VolumeFileCommands.Move, "Verschoben/umbenannt.", ct);

    public Task<(bool Ok, string Message)> CopyAsync(DockerEnvironment env, string volume, string srcRel, string dstRel, CancellationToken ct = default)
        => Mutate2Async(env, volume, srcRel, dstRel, VolumeFileCommands.Copy, "Kopiert.", ct);

    // ---- shared plumbing ----

    private async Task<(bool Ok, string Message)> MutateAsync(DockerEnvironment env, string volume, string relPath,
        Func<string, string, string, string, string> build, string okMessage, CancellationToken ct)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return (false, "Ungültiger Volume-Name.");
        }

        var rel = VolumeFileCommands.NormalizeRelPath(relPath);
        if (rel is null)
        {
            return (false, "Ungültiger Pfad.");
        }

        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                var result = await RunAsync(client, build(head, _options.HelperImage, volume, rel), ct);
                return result.ExitStatus == 0 ? (true, okMessage) : (false, MapError(result));
            }
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    private async Task<(bool Ok, string Message)> Mutate2Async(DockerEnvironment env, string volume, string srcRel, string dstRel,
        Func<string, string, string, string, string, string> build, string okMessage, CancellationToken ct)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return (false, "Ungültiger Volume-Name.");
        }

        var src = VolumeFileCommands.NormalizeRelPath(srcRel);
        var dst = VolumeFileCommands.NormalizeRelPath(dstRel);
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
        {
            return (false, "Ungültiger Quell- oder Zielpfad.");
        }

        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                var result = await RunAsync(client, build(head, _options.HelperImage, volume, src, dst), ct);
                return result.ExitStatus == 0 ? (true, okMessage) : (false, MapError(result));
            }
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    private async Task<(bool Ok, string Message)> MutateWithStdinAsync(DockerEnvironment env, string volume, string relPath, string content, CancellationToken ct)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return (false, "Ungültiger Volume-Name.");
        }

        var rel = VolumeFileCommands.NormalizeRelPath(relPath);
        if (string.IsNullOrEmpty(rel))
        {
            return (false, "Ungültiger Pfad.");
        }

        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                using var cmd = client.CreateCommand(VolumeFileCommands.Write(head, _options.HelperImage, volume, rel));
                cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(10, _options.SshTimeoutSeconds));
                // SSH.NET 2026: create the input stream only AFTER BeginExecute opened the channel.
                var async = cmd.BeginExecute();
                var input = cmd.CreateInputStream();
                var bytes = Encoding.UTF8.GetBytes(content);
                await input.WriteAsync(bytes, ct);
                input.Close();
                cmd.EndExecute(async);
                return cmd.ExitStatus == 0 ? (true, "Gespeichert.") : (false, "Speichern fehlgeschlagen.");
            }
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    private async Task<(string Head, SshClient Client)> ConnectAsync(DockerEnvironment env, CancellationToken ct)
    {
        var settings = _environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var client = _sshClientFactory.Create(settings);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
        await client.ConnectAsync(cts.Token);
        return (head, client);
    }

    private async Task<(int ExitStatus, string StdOut, string StdErr)> RunAsync(SshClient client, string command, CancellationToken ct)
    {
        using var cmd = client.CreateCommand(command);
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.SshTimeoutSeconds));
        cmd.CommandTimeout = timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await cmd.ExecuteAsync(cts.Token);
        return (cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
    }

    private static string MapError((int ExitStatus, string StdOut, string StdErr) r)
    {
        var err = r.StdErr;
        if (err.Contains("EXISTS")) return "Existiert bereits.";
        if (err.Contains("NOTAFILE")) return "Keine reguläre Datei.";
        return DockerErrorMessages.FirstLine(err) ?? "Aktion fehlgeschlagen.";
    }

    private string Describe(Exception ex)
    {
        _logger.LogInformation(ex, "Volume file operation failed.");
        return DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Fehler: {ex.Message}";
    }

    private static IReadOnlyList<VolumeFileEntry> ParseListing(string output)
    {
        var entries = new List<VolumeFileEntry>();
        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var isDir = parts[0] == "d";
            long.TryParse(parts[1], out var size);
            var name = string.Join('\t', parts[2..]); // names never contain tab in our emit
            entries.Add(new VolumeFileEntry(name, isDir, size));
        }

        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
