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

    // Non-interactive SSH sessions often have a minimal PATH; make sure docker is found.
    public const string PathPrefix =
        "export PATH=\"$PATH:/usr/local/bin:/usr/bin:/bin:/snap/bin:/usr/sbin:/sbin\"; ";

    public static bool IsValidVolumeName(string? name)
        => !string.IsNullOrEmpty(name) && VolumeNameRegex().IsMatch(name);

    public static bool IsValidImage(string? image)
        => !string.IsNullOrEmpty(image) && ImageRegex().IsMatch(image);

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

    public static string Create(string volume)
    {
        if (!IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Ungültiger Volume-Name: '{volume}'.", nameof(volume));
        }

        return PathPrefix + $"docker volume create '{volume}'";
    }

    /// <summary>Command that tars the volume contents to stdout (source side).</summary>
    public static string Export(string volume, string image)
    {
        EnsureValid(volume, image);
        return PathPrefix + $"docker run --rm -v '{volume}':/from:ro '{image}' tar -C /from -cf - .";
    }

    /// <summary>Command that untars stdin into the volume (target side).</summary>
    public static string Import(string volume, string image)
    {
        EnsureValid(volume, image);
        return PathPrefix + $"docker run --rm -i -v '{volume}':/to '{image}' tar -C /to -xf -";
    }

    /// <summary>Counts entries in a volume; used to detect a non-empty target before overwriting.</summary>
    public static string CountEntries(string volume, string image)
    {
        EnsureValid(volume, image);
        return PathPrefix + $"docker run --rm -v '{volume}':/data:ro '{image}' sh -c 'ls -A /data | wc -l'";
    }
}
