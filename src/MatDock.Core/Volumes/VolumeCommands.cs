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
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}$")]
    private static partial Regex VolumeNameRegex();

    // Conservative image reference (name[:tag] with optional registry/path); no shell metacharacters.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_./-]*(:[a-zA-Z0-9_.-]+)?$")]
    private static partial Regex ImageRegex();

    // A DOCKER_HOST value: unix:///path or tcp://host[:port]; no shell metacharacters.
    [GeneratedRegex(@"^(unix://\/[a-zA-Z0-9_./-]+|tcp://[a-zA-Z0-9_.:-]+)$")]
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
                throw new ArgumentException($"Ungültiger DOCKER_HOST: '{dockerHost}'.", nameof(dockerHost));
            }

            head += $"DOCKER_HOST={dockerHost} ";
        }

        return head + "docker";
    }

    public static void EnsureValid(string volume, string image)
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Ungültiger Volume-Name: '{volume}'.", nameof(volume));
        }

        if (!IsValidImage(image))
        {
            throw new ArgumentException($"Ungültiges Image: '{image}'.", nameof(image));
        }
    }

    public static string Create(string volume, string dockerHead = "docker")
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Ungültiger Volume-Name: '{volume}'.", nameof(volume));
        }

        return PathPrefix + $"{dockerHead} volume create '{volume}'";
    }

    /// <summary>Command that tars the volume contents to stdout (source side).</summary>
    public static string Export(string volume, string image, string dockerHead = "docker")
    {
        EnsureValid(volume, image);
        return PathPrefix + $"{dockerHead} run --rm -v '{volume}':/from:ro '{image}' tar -C /from -cf - .";
    }

    /// <summary>Command that untars stdin into the volume (target side).</summary>
    public static string Import(string volume, string image, string dockerHead = "docker")
    {
        EnsureValid(volume, image);
        return PathPrefix + $"{dockerHead} run --rm -i -v '{volume}':/to '{image}' tar -C /to -xf -";
    }

    /// <summary>Counts entries in a volume; used to detect a non-empty target before overwriting.</summary>
    public static string CountEntries(string volume, string image, string dockerHead = "docker")
    {
        EnsureValid(volume, image);
        return PathPrefix + $"{dockerHead} run --rm -v '{volume}':/data:ro '{image}' sh -c 'ls -A /data | wc -l'";
    }
}
