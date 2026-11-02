using System.Text.RegularExpressions;

namespace MatDock.Core.Updates;

/// <summary>
/// What the labels of a container say about its updates: which version tags count (<c>matdock.tag.include</c> / <c>matdock.tag.exclude</c>, also the
/// <c>dockhand.*</c> and What's Up Docker <c>wud.*</c> names; MatDock's own win), and where the release notes are (<c>matdock.changelog.url</c>, else the
/// source repository the image names in <c>org.opencontainers.image.source</c>).
/// </summary>
public static class UpdateLabels
{
    private static readonly string[] Prefixes = ["matdock", "dockhand", "wud"];

    public static string? Include(IReadOnlyDictionary<string, string>? labels) => First(labels, "tag.include");

    public static string? Exclude(IReadOnlyDictionary<string, string>? labels) => First(labels, "tag.exclude");

    /// <summary>The address of the release notes: the label's own, else the releases of a GitHub, GitLab, Codeberg or Gitea repository the image names.</summary>
    public static string? Changelog(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null)
        {
            return null;
        }

        foreach (var prefix in Prefixes.Take(2))
        {
            if (labels.TryGetValue(prefix + ".changelog.url", out var own) && IsHttps(own))
            {
                return own.Trim();
            }
        }

        if (!labels.TryGetValue("org.opencontainers.image.source", out var source) || !IsHttps(source))
        {
            return null;
        }

        var repo = source.Trim().TrimEnd('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        var host = new Uri(repo).Host.ToLowerInvariant();
        return host switch
        {
            "github.com" or "codeberg.org" or "gitea.com" => repo + "/releases",
            "gitlab.com" => repo + "/-/releases",
            _ => repo,
        };
    }

    /// <summary>
    /// The tags that pass the include and exclude patterns (unanchored regular expressions; exclude wins). A pattern that is too long, holds a variable or
    /// does not compile is ignored, and every match has a time limit, so a label cannot stall the check.
    /// </summary>
    public static IEnumerable<string> Filter(IEnumerable<string> tags, string? include, string? exclude)
    {
        var inc = Compile(include);
        var exc = Compile(exclude);
        return tags.Where(t => (inc is null || Matches(inc, t)) && (exc is null || !Matches(exc, t)));
    }

    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 300 || pattern.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Matches(Regex regex, string tag)
    {
        try
        {
            return regex.IsMatch(tag);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? First(IReadOnlyDictionary<string, string>? labels, string name)
    {
        if (labels is null)
        {
            return null;
        }

        foreach (var prefix in Prefixes)
        {
            if (labels.TryGetValue($"{prefix}.{name}", out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool IsHttps(string? url)
        => url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
