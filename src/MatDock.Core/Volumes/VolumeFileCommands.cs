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
    /// the path is unsafe (contains <c>..</c>, a NUL, CR/LF, or a <c>:</c> — which would be a Windows
    /// drive letter / alternate-data-stream and could root a local <c>Path.Combine</c>). Empty/'/'/'.'
    /// all normalize to root ("").
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

            if (raw.Contains('\0') || raw.Contains('\n') || raw.Contains('\r') || raw.Contains(':'))
            {
                return null; // control chars break paths; ':' would be a drive letter / ADS root
            }

            segments.Add(raw);
        }

        return string.Join("/", segments);
    }

    /// <summary>The absolute in-container path for a normalized relative path (the volume is mounted at /data).</summary>
    public static string ContainerPath(string relPath)
        => relPath.Length == 0 ? "/data" : "/data/" + relPath;

    /// <summary>The absolute path of a normalized relative path in the target: below /data for a volume, below / in a container.</summary>
    public static string TargetPath(FileTarget target, string relPath)
        => target.IsContainer ? "/" + relPath : ContainerPath(relPath);

    /// <summary>
    /// How a command reaches the files: a volume is mounted into a throwaway helper container (read-only where nothing is
    /// written), a container runs it itself (<c>docker exec</c>, so it needs its own shell and core tools).
    /// </summary>
    private static string Head(string dockerHead, string image, FileTarget target, bool readOnly, bool stdin)
    {
        var i = stdin ? "-i " : string.Empty;
        if (target.IsContainer)
        {
            return VolumeCommands.PathPrefix + dockerHead + " exec " + i + ShellQuote(target.Name) + " ";
        }

        var mount = ShellQuote(target.Name) + (readOnly ? ":/data:ro" : ":/data");
        return VolumeCommands.PathPrefix + dockerHead + " run --rm " + i + "-v " + mount + " " + ShellQuote(image) + " ";
    }

    public static string List(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: true, stdin: false)
           + "sh -c " + ShellQuote(ListScript) + " sh " + ShellQuote(TargetPath(target, relPath));

    /// <summary>Reads up to <see cref="MaxReadBytes"/>+1 bytes (the extra byte flags truncation).</summary>
    public static string Read(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: true, stdin: false)
           + "sh -c " + ShellQuote("[ -f \"$1\" ] || { echo NOTAFILE >&2; exit 9; }; head -c " + (MaxReadBytes + 1) + " -- \"$1\"")
           + " sh " + ShellQuote(TargetPath(target, relPath));

    /// <summary>The whole file (downloads): no size cap.</summary>
    public static string ReadAll(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: true, stdin: false)
           + "sh -c " + ShellQuote("[ -f \"$1\" ] || { echo NOTAFILE >&2; exit 9; }; cat -- \"$1\"")
           + " sh " + ShellQuote(TargetPath(target, relPath));

    /// <summary>
    /// Writes stdin to the file atomically: stream into a temp file in the same dir, then <c>mv</c> it
    /// into place, so an interrupted transfer leaves the original intact instead of truncating it.
    /// </summary>
    public static string Write(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: false, stdin: true)
           + "sh -c " + ShellQuote("tmp=\"$1.$$.matdock.tmp\"; cat > \"$tmp\" && mv -- \"$tmp\" \"$1\"")
           + " sh " + ShellQuote(TargetPath(target, relPath));

    public static string MakeDir(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: false, stdin: false)
           + "sh -c " + ShellQuote("[ -e \"$1\" ] && { echo EXISTS >&2; exit 17; }; mkdir -p -- \"$1\"")
           + " sh " + ShellQuote(TargetPath(target, relPath));

    /// <summary>Creates an empty file; fails (exit 17) if it already exists.</summary>
    public static string CreateFile(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: false, stdin: false)
           + "sh -c " + ShellQuote("[ -e \"$1\" ] && { echo EXISTS >&2; exit 17; }; : > \"$1\"")
           + " sh " + ShellQuote(TargetPath(target, relPath));

    public static string Delete(string dockerHead, string image, FileTarget target, string relPath)
        => Head(dockerHead, image, target, readOnly: false, stdin: false)
           + "rm -rf -- " + ShellQuote(TargetPath(target, relPath));

    public static string Move(string dockerHead, string image, FileTarget target, string srcRel, string dstRel)
        => Head(dockerHead, image, target, readOnly: false, stdin: false)
           + "mv -- " + ShellQuote(TargetPath(target, srcRel)) + " " + ShellQuote(TargetPath(target, dstRel));

    public static string Copy(string dockerHead, string image, FileTarget target, string srcRel, string dstRel)
        => Head(dockerHead, image, target, readOnly: false, stdin: false)
           + "cp -a -- " + ShellQuote(TargetPath(target, srcRel)) + " " + ShellQuote(TargetPath(target, dstRel));

    // The same for a volume by name.
    public static string List(string dockerHead, string image, string volume, string relPath) => List(dockerHead, image, FileTarget.ForVolume(volume), relPath);
    public static string Read(string dockerHead, string image, string volume, string relPath) => Read(dockerHead, image, FileTarget.ForVolume(volume), relPath);
    public static string Write(string dockerHead, string image, string volume, string relPath) => Write(dockerHead, image, FileTarget.ForVolume(volume), relPath);
    public static string MakeDir(string dockerHead, string image, string volume, string relPath) => MakeDir(dockerHead, image, FileTarget.ForVolume(volume), relPath);
    public static string CreateFile(string dockerHead, string image, string volume, string relPath) => CreateFile(dockerHead, image, FileTarget.ForVolume(volume), relPath);
    public static string Delete(string dockerHead, string image, string volume, string relPath) => Delete(dockerHead, image, FileTarget.ForVolume(volume), relPath);
    public static string Move(string dockerHead, string image, string volume, string srcRel, string dstRel) => Move(dockerHead, image, FileTarget.ForVolume(volume), srcRel, dstRel);
    public static string Copy(string dockerHead, string image, string volume, string srcRel, string dstRel) => Copy(dockerHead, image, FileTarget.ForVolume(volume), srcRel, dstRel);
}

/// <summary>Where the file browser works: the files of a volume, or the file system of a container.</summary>
public sealed record FileTarget(bool IsContainer, string Name)
{
    public static FileTarget ForVolume(string? volume) => new(false, volume ?? string.Empty);

    public static FileTarget ForContainer(string? id) => new(true, id ?? string.Empty);

    public bool IsValid => IsContainer ? Containers.ContainerCommands.IsValidId(Name) : VolumeCommands.IsValidVolumeName(Name);

    public string InvalidMessage => IsContainer ? "Invalid container." : "Invalid volume name.";
}
