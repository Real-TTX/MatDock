using System.Text.RegularExpressions;

namespace MatDock.Core.Volumes;

/// <summary>
/// Pure builders for the remote Docker commands used by volume operations. Volume names and image
/// names are strictly validated first, so the strings can be embedded in the SSH command line without
/// risking shell injection.
/// </summary>
public static partial class VolumeCommands
{
    // Docker volume naming rules: start alphanumeric, then [a-zA-Z0-9_.-].
    // \z (not $) so a trailing newline is rejected — $ would match just before a final \n.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}\z")]
    private static partial Regex VolumeNameRegex();

    // Conservative image reference (name[:tag] with optional registry/path); no shell metacharacters.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_./-]*(:[a-zA-Z0-9_.-]+)?\z")]
    private static partial Regex ImageRegex();

    // A DOCKER_HOST value: unix:///path or tcp://host[:port]; no shell metacharacters.
    // This value is embedded UNQUOTED (DOCKER_HOST=...), so \z is important: $ would accept a
    // trailing newline that could split the shell line.
    [GeneratedRegex(@"^(unix://\/[a-zA-Z0-9_./-]+|tcp://[a-zA-Z0-9_.:-]+)\z")]
    private static partial Regex DockerHostRegex();

    // Non-interactive SSH sessions often have a minimal PATH; make sure docker is found.
    public const string PathPrefix =
        "export PATH=\"$PATH:/usr/local/bin:/usr/bin:/bin:/snap/bin:/usr/sbin:/sbin\"; ";

    public static bool IsValidVolumeName(string? name)
        => !string.IsNullOrEmpty(name) && VolumeNameRegex().IsMatch(name);

    public static bool IsValidImage(string? image)
        => !string.IsNullOrEmpty(image) && ImageRegex().IsMatch(image);

    public static bool IsValidDockerHost(string? dockerHost)
        => !string.IsNullOrEmpty(dockerHost) && DockerHostRegex().IsMatch(dockerHost);

    /// <summary>
    /// Builds the validated docker invocation head, e.g. <c>docker</c>, <c>sudo -n docker</c> or
    /// <c>DOCKER_HOST=unix:///run/user/1000/docker.sock docker</c>. Everything is validated so the
    /// result is safe to embed in a shell command line.
    /// </summary>
    public static string DockerHead(bool useSudo, string? dockerHost)
    {
        var head = string.Empty;
        if (useSudo)
        {
            head += "sudo -n ";
        }

        if (!string.IsNullOrEmpty(dockerHost))
        {
            if (!IsValidDockerHost(dockerHost))
            {
                throw new ArgumentException($"Invalid DOCKER_HOST: '{dockerHost}'.", nameof(dockerHost));
            }

            head += $"DOCKER_HOST={dockerHost} ";
        }

        return head + "docker";
    }

    public static void EnsureValid(string volume, string image)
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Invalid volume name: '{volume}'.", nameof(volume));
        }

        if (!IsValidImage(image))
        {
            throw new ArgumentException($"Invalid image: '{image}'.", nameof(image));
        }
    }

    public static string Create(string volume, string dockerHead = "docker")
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Invalid volume name: '{volume}'.", nameof(volume));
        }

        return PathPrefix + $"{dockerHead} volume create '{volume}'";
    }

    // Driver and --opt keys: letters, digits, underscore, dot, hyphen (no shell metacharacters).
    [GeneratedRegex(@"^[a-zA-Z0-9_.-]+\z")]
    private static partial Regex OptKeyRegex();

    /// <summary>
    /// Builds <c>docker volume create [--driver &lt;driver&gt;] [--opt &lt;k&gt;='&lt;v&gt;']... '&lt;name&gt;'</c>.
    /// Name, driver and option keys are validated; option VALUES (paths, addresses, credentials) are
    /// single-quoted so they may contain arbitrary characters without breaking the shell line.
    /// </summary>
    public static string CreateWithOptions(string volume, string? driver, IReadOnlyList<(string Key, string Value)> options, string dockerHead = "docker")
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Invalid volume name: '{volume}'.", nameof(volume));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(PathPrefix).Append(dockerHead).Append(" volume create");

        if (!string.IsNullOrWhiteSpace(driver))
        {
            if (!OptKeyRegex().IsMatch(driver))
            {
                throw new ArgumentException($"Invalid driver: '{driver}'.", nameof(driver));
            }
            sb.Append(" --driver ").Append(driver);
        }

        foreach (var (key, value) in options)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            if (!OptKeyRegex().IsMatch(key))
            {
                throw new ArgumentException($"Invalid option key: '{key}'.", nameof(options));
            }
            sb.Append(" --opt ").Append(key).Append('=').Append(VolumeFileCommands.ShellQuote(value ?? string.Empty));
        }

        sb.Append(" '").Append(volume).Append('\'');
        return sb.ToString();
    }

    /// <summary>Command that tars the volume contents to stdout (source side).</summary>
    public static string Export(string volume, string image, string dockerHead = "docker")
    {
        EnsureValid(volume, image);
        return PathPrefix + $"{dockerHead} run --rm -v '{volume}':/from:ro '{image}' tar -C /from -cf - .";
    }

    /// <summary>
    /// Command that untars stdin into the volume (target side). When <paramref name="clearFirst"/> is
    /// true the target contents are wiped before extraction, so the result matches the archive exactly
    /// (used for "overwrite").
    /// </summary>
    public static string Import(string volume, string image, string dockerHead = "docker", bool clearFirst = false)
    {
        EnsureValid(volume, image);
        return clearFirst
            ? PathPrefix + $"{dockerHead} run --rm -i -v '{volume}':/to '{image}' sh -c 'rm -rf /to/* /to/.[!.]* /to/..?* 2>/dev/null; exec tar -C /to -xf -'"
            : PathPrefix + $"{dockerHead} run --rm -i -v '{volume}':/to '{image}' tar -C /to -xf -";
    }

    /// <summary>Verifies a volume exists (exit code 0) without creating it.</summary>
    public static string Inspect(string volume, string dockerHead = "docker")
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Invalid volume name: '{volume}'.", nameof(volume));
        }

        return PathPrefix + $"{dockerHead} volume inspect '{volume}'";
    }

    /// <summary>Full volume detail as a single JSON object (driver, mountpoint, options, labels).</summary>
    public static string InspectJson(string volume, string dockerHead = "docker")
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Invalid volume name: '{volume}'.", nameof(volume));
        }

        // Braces built by concatenation so the Go template survives (see note below).
        return PathPrefix + dockerHead + " volume inspect '" + volume + "' --format '{{json .}}'";
    }

    /// <summary>Counts entries in a volume; used to detect a non-empty target before overwriting.</summary>
    public static string CountEntries(string volume, string image, string dockerHead = "docker")
    {
        EnsureValid(volume, image);
        return PathPrefix + $"{dockerHead} run --rm -v '{volume}':/data:ro '{image}' sh -c 'ls -A /data | wc -l'";
    }

    // NOTE: the Go-template braces below are built by concatenation (not string interpolation) on
    // purpose — in an interpolated string "{{json .}}" collapses to "{json .}" and breaks the command.
    public static string ServerVersion(string dockerHead = "docker")
        => PathPrefix + dockerHead + " version --format '{{json .Server}}'";

    public static string VolumeList(string dockerHead = "docker")
        => PathPrefix + dockerHead + " volume ls --format '{{json .}}'";

    /// <summary>Names (one per line) of volumes not referenced by any container — Docker's "dangling" filter.</summary>
    public static string VolumeListDangling(string dockerHead = "docker")
        => PathPrefix + dockerHead + " volume ls -q --filter dangling=true";

    /// <summary>Removes all unused (dangling) volumes.</summary>
    public static string Prune(string dockerHead = "docker")
        => PathPrefix + dockerHead + " volume prune -f";
}
