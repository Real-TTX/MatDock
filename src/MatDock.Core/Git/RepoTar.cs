using System.Formats.Tar;
using System.IO.Enumeration;

namespace MatDock.Core.Git;

/// <summary>
/// Packs the clone of a repository for the host. <see cref="TarFile.CreateFromDirectory(string, Stream, bool)"/> leaves out the files Linux counts as
/// hidden (every name starting with a dot), so a repository's <c>.env</c> never reached the host; this one takes every file and directory. A symbolic link
/// goes along as a link (it is not followed, also not into a linked directory), as the extract on the host would make it anyway.
/// </summary>
public static class RepoTar
{
    public static void Write(string directory, Stream destination)
    {
        var root = Path.GetFullPath(directory);
        using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        var entries = new FileSystemEnumerable<(string Path, bool IsDirectory)>(root,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0),
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
        {
            // Into real directories only: a link to a directory is written as the link it is.
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        foreach (var (path, isDirectory) in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(root, path).Replace('\\', '/');
            writer.WriteEntry(path, isDirectory ? name + "/" : name);
        }
    }
}
