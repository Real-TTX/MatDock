using System.Text.RegularExpressions;

namespace MatDock.Core.Containers;

/// <summary>
/// The public addresses a container's labels name: its own (<c>matdock.url</c>, <c>dockhand.url</c>), the hosts of its Traefik routers
/// (<c>traefik.http.routers.*.rule=Host(`app.example.com`)</c>, https unless the router listens only on a plain entry point) and the sites of
/// caddy-docker-proxy (<c>caddy=app.example.com</c>, <c>caddy_0=…</c>). The lists show them as links beside the ports.
/// </summary>
public static partial class ProxyUrls
{
    private const int Max = 6;

    [GeneratedRegex(@"Host\(\s*([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex HostRule();

    [GeneratedRegex(@"[`""']([A-Za-z0-9.-]+)[`""']")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^caddy(_\d+)?$")]
    private static partial Regex CaddyLabel();

    [GeneratedRegex(@"^(\*\.)?[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex Domain();

    public static IReadOnlyList<string> From(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return [];
        }

        var urls = new List<string>();
        void Add(string url)
        {
            if (urls.Count < Max && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        foreach (var key in new[] { "matdock.url", "dockhand.url" })
        {
            if (labels.TryGetValue(key, out var own) && Uri.TryCreate(own.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                Add(uri.AbsoluteUri.TrimEnd('/'));
            }
        }

        foreach (var (key, value) in labels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            if (key.StartsWith("traefik.http.routers.", StringComparison.Ordinal) && key.EndsWith(".rule", StringComparison.Ordinal))
            {
                var router = key["traefik.http.routers.".Length..^".rule".Length];
                var plain = labels.TryGetValue($"traefik.http.routers.{router}.entrypoints", out var entry)
                            && !labels.ContainsKey($"traefik.http.routers.{router}.tls")
                            && entry.Split(',').All(e => e.Trim() is "web" or "http");
                foreach (Match rule in HostRule().Matches(value))
                {
                    foreach (Match host in Quoted().Matches(rule.Groups[1].Value))
                    {
                        if (Domain().IsMatch(host.Groups[1].Value))
                        {
                            Add($"{(plain ? "http" : "https")}://{host.Groups[1].Value.ToLowerInvariant()}");
                        }
                    }
                }
            }
            else if (CaddyLabel().IsMatch(key))
            {
                foreach (var site in value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = site.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
                    if (Domain().IsMatch(name) && !name.StartsWith("*.", StringComparison.Ordinal))
                    {
                        Add($"https://{name.ToLowerInvariant()}");
                    }
                }
            }
        }

        return urls;
    }
}
