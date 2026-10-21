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
    // --digests populates the RepoDigest (sha256) so update detection can compare it to the registry.
    public static string List(string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image ls --digests --format '{{json .}}'";

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

    /// <summary>Pulls an image by reference (<c>docker pull</c>); output merged so progress/errors are captured.</summary>
    public static string Pull(string reference, string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " pull " + QuotedRef(reference) + " 2>&1";

    /// <summary>
    /// The tag that keeps the image of <paramref name="reference"/> during an update (<c>repo:matdock-rollback-latest</c>): with the containerd image
    /// store an image that loses its last name is gone, so the old one gets this name before the pull and its tag back from it on a rollback.
    /// Null for a reference pinned to a digest (nothing moves).
    /// </summary>
    public static string? RollbackReference(string reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Contains('@'))
        {
            return null;
        }

        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        var (repo, tag) = colon > slash ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
        var backup = "matdock-rollback-" + tag;
        return repo + ":" + (backup.Length > 128 ? backup[..128] : backup);
    }

    /// <summary>Removes the tag <paramref name="reference"/> (the image stays while another name or a container holds it).</summary>
    public static string Untag(string reference, string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image rm " + QuotedRef(reference);

    /// <summary>Puts the tag <paramref name="target"/> on the image <paramref name="source"/> (an id or a reference).</summary>
    public static string Tag(string source, string target, string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image tag " + QuotedRef(source) + " " + QuotedRef(target);

    /// <summary>The image as one JSON line (<c>docker image inspect</c>).</summary>
    public static string Inspect(string reference, string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image inspect --format '{{json .}}' " + QuotedRef(reference);

    /// <summary>The layers of the image, newest first, one JSON line each (<c>docker image history</c>).</summary>
    public static string History(string reference, string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " image history --no-trunc --format '{{json .}}' " + QuotedRef(reference);

    private static string QuotedRef(string reference)
        => IsValidImageRef(reference)
            ? VolumeFileCommands.ShellQuote(reference)
            : throw new ArgumentException($"Invalid image reference: '{reference}'.", nameof(reference));
}
