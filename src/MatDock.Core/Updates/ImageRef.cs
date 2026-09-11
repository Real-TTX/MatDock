namespace MatDock.Core.Updates;

/// <summary>
/// A parsed container image reference: registry host, repository and tag (or a pinned digest).
/// Follows Docker's rules: a first path segment containing '.', ':' or equal to "localhost" is the
/// registry host; otherwise the host defaults to docker.io and a single-segment repo is prefixed
/// with "library/". A <c>@sha256:</c> reference is treated as pinned (no moving-tag update).
/// </summary>
public sealed record ImageRef(string Host, string Repository, string Tag, string? Digest)
{
    /// <summary>True when the reference is pinned to a digest (…@sha256:…) — not a moving tag.</summary>
    public bool IsDigestPinned => Digest is not null;

    public static ImageRef? TryParse(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var text = image.Trim();

        // Split off an @digest suffix first (repo[:tag]@sha256:...).
        string? digest = null;
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            digest = text[(at + 1)..];
            text = text[..at];
        }

        // Determine the registry host: the first '/'-segment if it looks like a host.
        string host = "docker.io";
        var slash = text.IndexOf('/');
        if (slash > 0)
        {
            var first = text[..slash];
            if (first.Contains('.') || first.Contains(':') || first == "localhost")
            {
                host = first;
                text = text[(slash + 1)..];
            }
        }

        // Remaining text is repo[:tag]. A ':' after the last '/' is the tag separator.
        var tag = "latest";
        var lastSlash = text.LastIndexOf('/');
        var colon = text.IndexOf(':', lastSlash + 1);
        if (colon >= 0)
        {
            tag = text[(colon + 1)..];
            text = text[..colon];
        }

        var repo = text;
        if (repo.Length == 0)
        {
            return null;
        }

        // Docker Hub official images live under library/.
        if (host is "docker.io" or "index.docker.io" && !repo.Contains('/'))
        {
            repo = "library/" + repo;
        }

        return new ImageRef(host, repo, tag, string.IsNullOrEmpty(digest) ? null : digest);
    }
}
