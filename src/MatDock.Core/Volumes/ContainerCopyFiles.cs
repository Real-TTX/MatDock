using System.Formats.Tar;
using System.Text;

namespace MatDock.Core.Volumes;

/// <summary>
/// The file browser for a container without a shell (distroless, scratch): <c>docker cp</c> carries a tar to and from the container, so
/// listing, reading, writing, uploading and new folders and files work; deleting, renaming and copying need a shell and are refused.
/// </summary>
public static class ContainerCopyFiles
{
    /// <summary>A listing stops after this many entries or bytes of the tar (a folder with a big tree below it).</summary>
    public const int MaxEntries = 20_000;
    public const long MaxBytes = 256L * 1024 * 1024;

    public const string NoShellMessage = "This container has no shell: files can be browsed, read and written, but not deleted, renamed or copied.";

    /// <summary>Whether a <c>docker exec … sh</c> failed because the container has no <c>sh</c>.</summary>
    public static bool IsNoShell(int exitStatus, string stderr)
        => exitStatus is 126 or 127
           || stderr.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
           || (stderr.Contains("OCI runtime exec failed", StringComparison.OrdinalIgnoreCase) && stderr.Contains("sh", StringComparison.Ordinal));

    /// <summary><c>docker exec id sh -c true</c>: does the container have a shell?</summary>
    public static string ShellProbe(string dockerHead, string container)
        => VolumeCommands.PathPrefix + dockerHead + " exec " + VolumeFileCommands.ShellQuote(container) + " sh -c true";

    /// <summary>A tar of <paramref name="absolutePath"/> on stdout; a folder as <c>path/.</c>, so its entries start with <c>./</c>.</summary>
    public static string CopyOut(string dockerHead, string container, string absolutePath, bool folder)
        => VolumeCommands.PathPrefix + dockerHead + " cp " + VolumeFileCommands.ShellQuote(container + ":" + (folder ? absolutePath.TrimEnd('/') + "/." : absolutePath)) + " -";

    /// <summary>Unpacks a tar from stdin into the folder <paramref name="absoluteFolder"/> of the container.</summary>
    public static string CopyIn(string dockerHead, string container, string absoluteFolder)
        => VolumeCommands.PathPrefix + dockerHead + " cp - " + VolumeFileCommands.ShellQuote(container + ":" + (absoluteFolder.Length == 0 ? "/" : absoluteFolder));

    /// <summary>The direct children of the folder from the tar of <see cref="CopyOut"/> (folder mode), and whether the listing was cut.</summary>
    public static (IReadOnlyList<VolumeFileEntry> Entries, bool Truncated) ListFromTar(Stream tar)
    {
        var entries = new Dictionary<string, VolumeFileEntry>(StringComparer.Ordinal);
        var counted = 0;
        long bytes = 0;
        using var reader = new TarReader(tar, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (++counted > MaxEntries || (bytes += entry.Length + 512) > MaxBytes)
            {
                return (Sorted(entries.Values), true);
            }

            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            name = name.TrimStart('/');
            if (name.Length == 0 || name == ".")
            {
                continue;
            }

            var slash = name.IndexOf('/');
            var first = slash < 0 ? name : name[..slash];
            var isDirectory = slash >= 0 || entry.EntryType is TarEntryType.Directory;
            if (!entries.ContainsKey(first) || isDirectory)
            {
                entries[first] = new VolumeFileEntry(first, isDirectory, isDirectory ? 0 : entry.Length);
            }
        }

        return (Sorted(entries.Values), false);
    }

    /// <summary>Copies the content of the first regular file of a tar (<see cref="CopyOut"/> of a file) to <paramref name="output"/>; false when there is none.</summary>
    public static async Task<bool> ExtractFileAsync(Stream tar, Stream output, long? maxBytes, CancellationToken ct)
    {
        using var reader = new TarReader(tar, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }

            if (maxBytes is { } max)
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while (copied < max && (read = await entry.DataStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - copied)), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                }
            }
            else
            {
                await entry.DataStream.CopyToAsync(output, ct);
            }

            return true;
        }

        return false;
    }

    /// <summary>A tar with one file <paramref name="name"/> (or a folder when <paramref name="content"/> is null) for <see cref="CopyIn"/>.</summary>
    public static async Task WriteTarAsync(Stream output, string name, Stream? content, CancellationToken ct)
    {
        await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        if (content is null)
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name.TrimEnd('/') + "/") { Mode = DefaultDirMode }, ct);
            return;
        }

        // The tar header needs the length first: a stream that cannot tell it is buffered.
        Stream data = content;
        if (!content.CanSeek)
        {
            var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            data = buffered;
        }

        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = DefaultFileMode, DataStream = data }, ct);
    }

    public static Task WriteTarAsync(Stream output, string name, string text, CancellationToken ct)
        => WriteTarAsync(output, name, new MemoryStream(Encoding.UTF8.GetBytes(text)), ct);

    private const UnixFileMode DefaultFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode DefaultDirMode = DefaultFileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static IReadOnlyList<VolumeFileEntry> Sorted(IEnumerable<VolumeFileEntry> entries)
        => entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
