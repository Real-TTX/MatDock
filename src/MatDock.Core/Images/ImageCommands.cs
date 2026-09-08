using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Images;

/// <summary>
/// Pure builders for the remote Docker commands used by image operations. An image reference (name:tag,
/// digest or id) is strictly validated first and then single-quoted, so it is safe to embed in the SSH
/// command line. Reuses <see cref="VolumeCommands.PathPrefix"/> and <see cref="VolumeCommands.DockerHead"/>.
/// </summary>
public static partial class ImageCommands
{
    // An image ref: registry[:port]/repo[:tag] | repo:tag | name@sha256:hex | sha256:hex | short id.
    // Deliberately conservative charset (no shell metacharacters, no quotes/spaces); \z rejects a trailing newline.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_./:@-]{0,318}\z")]
    private static partial Regex ImageRefRegex();

    /// <summary>A valid image reference or id (safe to embed after quoting).</summary>
    public static bool IsValidImageRef(string? reference)
        => !string.IsNullOrEmpty(reference) && ImageRefRegex().IsMatch(reference);

    // NOTE: the Go-template braces are built by concatenation (not interpolation) so "{{json .}}" survives.
    public static string List(string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image ls --format '{{json .}}'";

    public static string Remove(string reference, bool force, string dockerHead = "docker")
    {
        if (!IsValidImageRef(reference))
        {
            throw new ArgumentException($"Invalid image reference: '{reference}'.", nameof(reference));
        }

        var flag = force ? " -f" : string.Empty;
        return VolumeCommands.PathPrefix + dockerHead + " image rm" + flag + " " + VolumeFileCommands.ShellQuote(reference);
    }

    /// <summary>Removes dangling images (<c>image prune -f</c>), or all unused images when <paramref name="all"/> is set.</summary>
    public static string Prune(bool all, string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image prune -f" + (all ? " -a" : string.Empty);
}
