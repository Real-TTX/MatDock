using System.Collections.Concurrent;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Volumes;

/// <summary>The file browser on a container without a shell, through <c>docker cp</c> (<see cref="ContainerCopyFiles"/>).</summary>
public sealed partial class VolumeFileService
{
    private static readonly TimeSpan ShellCacheTime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<(long Env, string Container), (bool NoShell, DateTime At)> ShellCache = new();

    /// <summary>Whether <paramref name="target"/> is a container without <c>sh</c> (asked once every few minutes per container).</summary>
    private async Task<bool> NoShellAsync(DockerEnvironment env, FileTarget target, CancellationToken ct)
    {
        if (!target.IsContainer || !target.IsValid)
        {
            return false;
        }

        var key = (env.Id, target.Name);
        if (ShellCache.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < ShellCacheTime)
        {
            return known.NoShell;
        }

        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                var probe = await RunAsync(client, ContainerCopyFiles.ShellProbe(head, target.Name), ct);
                var noShell = probe.ExitStatus != 0 && ContainerCopyFiles.IsNoShell(probe.ExitStatus, probe.StdErr + probe.StdOut);
                // Remembered only when the answer is sure (a stopped container says neither).
                if (probe.ExitStatus == 0 || noShell)
                {
                    ShellCache[key] = (noShell, DateTime.UtcNow);
                }

                return noShell;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Shell probe of {Container} failed.", target.Name);
            return false; // the normal way reports the problem
        }
    }

    private async Task<(IReadOnlyList<VolumeFileEntry> Entries, string? Error)> ListViaCopyAsync(DockerEnvironment env, FileTarget target, string rel, CancellationToken ct)
    {
        IReadOnlyList<VolumeFileEntry> entries = Array.Empty<VolumeFileEntry>();
        var truncated = false;
        var (ok, error) = await CopyOutAsync(env, target, rel, folder: true, s =>
        {
            (entries, truncated) = ContainerCopyFiles.ListFromTar(s);
            return Task.FromResult(true);
        }, ct, stopEarly: () => truncated);
        if (!ok)
        {
            return (Array.Empty<VolumeFileEntry>(), string.IsNullOrEmpty(error) ? "Directory could not be read." : error);
        }

        return (entries, truncated ? "This folder holds too much to list it without a shell; only a part is shown." : null);
    }

    private async Task<(FileReadResult? File, string? Error)> ReadViaCopyAsync(DockerEnvironment env, FileTarget target, string rel, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var (ok, error) = await CopyOutAsync(env, target, rel, folder: false,
            s => ContainerCopyFiles.ExtractFileAsync(s, buffer, VolumeFileCommands.MaxReadBytes + 1, ct), ct, stopEarly: () => true);
        return ok ? (ToReadResult(buffer.ToArray()), null) : (null, string.IsNullOrEmpty(error) ? "File could not be read (does it exist and is it a regular file?)." : error);
    }

    /// <summary>Runs <c>docker cp</c> out of the container and hands its tar to <paramref name="consume"/> (false = no such file).</summary>
    private async Task<(bool Ok, string Error)> CopyOutAsync(DockerEnvironment env, FileTarget target, string rel, bool folder,
        Func<Stream, Task<bool>> consume, CancellationToken ct, Func<bool>? stopEarly = null)
    {
        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                using var cmd = client.CreateCommand(ContainerCopyFiles.CopyOut(head, target.Name, VolumeFileCommands.TargetPath(target, rel), folder));
                cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(300, _options.SshTimeoutSeconds));
                cmd.BufferOutput = false;
                var running = cmd.BeginExecute();
                bool found;
                try
                {
                    found = await consume(cmd.OutputStream);
                }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or EndOfStreamException or FormatException)
                {
                    found = false; // no tar came: the path does not exist
                }

                var stoppedEarly = stopEarly?.Invoke() == true;
                if (stoppedEarly)
                {
                    // The rest of the tar is not needed: closing the command ends docker cp.
                    cmd.CommandTimeout = TimeSpan.FromSeconds(2);
                }
                else
                {
                    await cmd.OutputStream.CopyToAsync(Stream.Null, ct);
                }

                try { cmd.EndExecute(running); } catch { /* ended early */ }
                var ok = found && (stoppedEarly || cmd.ExitStatus == 0);
                return ok ? (true, string.Empty) : (false, DockerErrorMessages.FirstLine(cmd.Error) ?? string.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>Sends a tar written by <paramref name="write"/> into the folder of <paramref name="relPath"/> with <c>docker cp</c>.</summary>
    private async Task<(bool Ok, string Message)> CopyInAsync(DockerEnvironment env, FileTarget target, string relPath,
        Func<Stream, CancellationToken, Task> write, string okMessage, CancellationToken ct)
    {
        var rel = VolumeFileCommands.NormalizeRelPath(relPath);
        if (string.IsNullOrEmpty(rel))
        {
            return (false, "Invalid path.");
        }

        var folder = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : string.Empty;
        try
        {
            var (head, client) = await ConnectAsync(env, ct);
            using (client)
            {
                using var cmd = client.CreateCommand(ContainerCopyFiles.CopyIn(head, target.Name, "/" + folder));
                cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(300, _options.SshTimeoutSeconds));
                var running = cmd.BeginExecute();
                var input = cmd.CreateInputStream();
                await write(input, ct);
                input.Close();
                cmd.EndExecute(running);
                return cmd.ExitStatus == 0 ? (true, okMessage) : (false, DockerErrorMessages.FirstLine(cmd.Error) ?? "Writing into the container failed.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Describe(ex));
        }
    }

    private static string Name(string relPath)
    {
        var rel = VolumeFileCommands.NormalizeRelPath(relPath) ?? relPath;
        return rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
    }
}
