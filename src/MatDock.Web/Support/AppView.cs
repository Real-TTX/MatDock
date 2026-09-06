using MatDock.Core.Apps;
using MatDock.Core.Containers;
using MatDock.Web.Controls;

namespace MatDock.Web.Support;

/// <summary>View helpers for rendering app metadata (icon + action links) on stack cards and the launchpad.</summary>
public static class AppView
{
    public enum IconKind { None, Image, Lucide, Glyph }

    public static AppMetadata? Parse(string? appMetaJson) => AppMetadataParser.FromJson(appMetaJson);

    /// <summary>Classifies an icon value so the view knows how to render it.</summary>
    public static IconKind KindOf(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return IconKind.None;
        }
        if (icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return IconKind.Image;
        }
        return Icons.TryGet(icon, out _) ? IconKind.Lucide : IconKind.Glyph;
    }

    /// <summary>
    /// Resolves an action's href: an absolute http(s) URL as-is, or a "/relative" path against the app
    /// host + a published port (the action's <c>port</c>, else the stack's primary port). Null if unresolvable.
    /// </summary>
    public static string? ActionHref(AppAction action, string? host, int? primaryPort)
    {
        var url = (action.Url ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u)
                   && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
                ? u.AbsoluteUri
                : null;
        }

        if (url.StartsWith('/'))
        {
            var port = action.Port ?? primaryPort;
            return host is not null && port is not null ? $"http://{host}:{port}{url}" : null;
        }

        return null;
    }

    /// <summary>The first published host port across a stack's containers (for the primary "open" link).</summary>
    public static int? PrimaryPort(IEnumerable<DockerContainer> containers)
    {
        foreach (var c in containers)
        {
            var p = FirstPublishedHostPort(c.Ports);
            if (p is not null)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Parses a docker ports string ("0.0.0.0:8096->8096/tcp, …") for the first published host port.</summary>
    public static int? FirstPublishedHostPort(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports))
        {
            return null;
        }

        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue; // exposed-only, not published
            }

            var left = part[..arrow];               // e.g. "0.0.0.0:8096" or ":::8096"
            var colon = left.LastIndexOf(':');
            var portText = colon >= 0 ? left[(colon + 1)..] : left;
            if (int.TryParse(portText, out var port) && port > 0)
            {
                return port;
            }
        }

        return null;
    }
}
