namespace MatDock.Core.Volumes;

/// <summary>
/// Builds the remote helper-container commands for browsing/editing files inside a Docker volume.
/// Everything runs in a throwaway <c>busybox</c> container with the volume mounted at <c>/data</c>.
/// Two safety layers: (1) <see cref="NormalizeRelPath"/> rejects any path that could escape the volume
/// (no <c>..</c>, no absolute breakout, no control chars); (2) <see cref="ShellQuote"/> POSIX-quotes
/// every value so arbitrary file names cannot inject shell syntax.
/// </summary>
public static class VolumeFileCommands
{
    /// <summary>Max bytes read for the in-browser text editor.</summary>
    public const int MaxReadBytes = 512 * 1024;

    // Listing script: emits "type<TAB>size<TAB>name" per entry (name last so spaces are fine).
    // The path is passed as $1 so it is never concatenated into the script text.
    private const string ListScript =
        "cd -- \"$1\" 2>/dev/null || exit 3; " +
        "for e in .* *; do " +
        "[ \"$e\" = . ] && continue; [ \"$e\" = .. ] && continue; " +
        "if [ -d \"$e\" ]; then printf 'd\\t0\\t%s\\n' \"$e\"; " +
        "elif [ -e \"$e\" ] || [ -L \"$e\" ]; then printf 'f\\t%s\\t%s\\n' \"$(wc -c < \"$e\" 2>/dev/null || echo 0)\" \"$e\"; fi; " +
        "done";

    /// <summary>POSIX single-quote quoting: safe for any string as one shell token.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Normalizes a volume-relative path to a safe form (segments joined by '/'), or returns null when
    /// the path is unsafe (contains <c>..</c>, a NUL, or CR/LF). Empty/'/'/'.' all normalize to root ("").
    /// </summary>
    public static string? NormalizeRelPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var segments = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/'))
        {
            if (raw.Length == 0 || raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                return null; // traversal attempt
            }

            if (raw.Contains('\0') || raw.Contains('\n') || raw.Contains('\r'))
            {
                return null; // control chars would break listing/paths
            }

            segments.Add(raw);
        }

        return string.Join("/", segments);
    }

    /// <summary>The absolute in-container path for a normalized relative path.</summary>
    public static string ContainerPath(string relPath)
        => relPath.Length == 0 ? "/data" : "/data/" + relPath;

    private static string Head(string dockerHead, string image, string volume, bool readOnly, bool stdin)
    {
        var mount = ShellQuote(volume) + (readOnly ? ":/data:ro" : ":/data");
        var i = stdin ? "-i " : string.Empty;
        return VolumeCommands.PathPrefix + dockerHead + " run --rm " + i + "-v " + mount + " " + ShellQuote(image) + " ";
    }

    public static string List(string dockerHead, string image, string volume, string relPath)
        => Head(dockerHead, image, volume, readOnly: true, stdin: false)
           + "sh -c " + ShellQuote(ListScript) + " sh " + ShellQuote(ContainerPath(relPath));

    /// <summary>Reads up to <see cref="MaxReadBytes"/>+1 bytes (the extra byte flags truncation).</summary>
    public static string Read(string dockerHead, string image, string volume, string relPath)
        => Head(dockerHead, image, volume, readOnly: true, stdin: false)
           + "sh -c " + ShellQuote("[ -f \"$1\" ] || { echo NOTAFILE >&2; exit 9; }; head -c " + (MaxReadBytes + 1) + " -- \"$1\"")
           + " sh " + ShellQuote(ContainerPath(relPath));

    /// <summary>
    /// Writes stdin to the file atomically: stream into a temp file in the same dir, then <c>mv</c> it
    /// into place, so an interrupted transfer leaves the original intact instead of truncating it.
    /// </summary>
    public static string Write(string dockerHead, string image, string volume, string relPath)
        => Head(dockerHead, image, volume, readOnly: false, stdin: true)
           + "sh -c " + ShellQuote("tmp=\"$1.$$.matdock.tmp\"; cat > \"$tmp\" && mv -- \"$tmp\" \"$1\"")
           + " sh " + ShellQuote(ContainerPath(relPath));

    public static string MakeDir(string dockerHead, string image, string volume, string relPath)
        => Head(dockerHead, image, volume, readOnly: false, stdin: false)
           + "sh -c " + ShellQuote("[ -e \"$1\" ] && { echo EXISTS >&2; exit 17; }; mkdir -p -- \"$1\"")
           + " sh " + ShellQuote(ContainerPath(relPath));

    /// <summary>Creates an empty file; fails (exit 17) if it already exists.</summary>
    public static string CreateFile(string dockerHead, string image, string volume, string relPath)
        => Head(dockerHead, image, volume, readOnly: false, stdin: false)
           + "sh -c " + ShellQuote("[ -e \"$1\" ] && { echo EXISTS >&2; exit 17; }; : > \"$1\"")
           + " sh " + ShellQuote(ContainerPath(relPath));

    public static string Delete(string dockerHead, string image, string volume, string relPath)
        => Head(dockerHead, image, volume, readOnly: false, stdin: false)
           + "rm -rf -- " + ShellQuote(ContainerPath(relPath));

    public static string Move(string dockerHead, string image, string volume, string srcRel, string dstRel)
        => Head(dockerHead, image, volume, readOnly: false, stdin: false)
           + "mv -- " + ShellQuote(ContainerPath(srcRel)) + " " + ShellQuote(ContainerPath(dstRel));

    public static string Copy(string dockerHead, string image, string volume, string srcRel, string dstRel)
        => Head(dockerHead, image, volume, readOnly: false, stdin: false)
           + "cp -a -- " + ShellQuote(ContainerPath(srcRel)) + " " + ShellQuote(ContainerPath(dstRel));
}
