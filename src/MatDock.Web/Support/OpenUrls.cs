namespace MatDock.Web.Support;

/// <summary>
/// Resolves the "open in browser" URL for a container: the per-container override (matdock.openurl label)
/// wins over the environment's base URL. Only absolute http/https URLs are returned — anything else
/// (e.g. a <c>javascript:</c> label value) yields null so it is never rendered as a link.
/// </summary>
public static class OpenUrls
{
    public static string? Resolve(string? containerOverride, string? environmentBaseUrl)
        => Sanitize(containerOverride) ?? Sanitize(environmentBaseUrl);

    /// <summary>
    /// The host to use for per-port links: the environment's base-URL host if configured, else "localhost"
    /// for a local environment, else the SSH host. Null when nothing usable is available.
    /// </summary>
    public static string? PortLinkHost(MatDock.Core.Entities.DockerEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(env.BaseUrl)
            && Uri.TryCreate(env.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.Host;
        }

        if (env.ConnectionType == MatDock.Core.Entities.ConnectionType.Local)
        {
            return "localhost";
        }

        return string.IsNullOrWhiteSpace(env.Host) ? null : env.Host;
    }

    private static string? Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }
}
