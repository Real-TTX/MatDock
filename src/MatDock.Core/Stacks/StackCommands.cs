using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Stacks;

/// <summary>
/// Builders for the remote <c>docker compose</c> commands of a managed stack. The stack name is a
/// strictly validated compose project name, so it is safe to embed; the compose YAML is delivered via
/// stdin (never interpolated into the command). Files live in <c>$HOME/.matdock/stacks/&lt;name&gt;</c>.
/// </summary>
public static partial class StackCommands
{
    // Compose project name: lowercase alphanumeric start, then [a-z0-9_-]. \z rejects a trailing newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}\z")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string? name) => !string.IsNullOrEmpty(name) && NameRegex().IsMatch(name);

    /// <summary>
    /// Derives a valid compose project name from a free-form title (e.g. a template name): lowercased,
    /// non-alphanumeric runs become '-', trimmed, capped at 63 chars, guaranteed to start alphanumeric.
    /// Falls back to "app" when nothing usable remains.
    /// </summary>
    public static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "app";
        }

        var sb = new System.Text.StringBuilder(title.Length);
        var lastDash = false;
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        // Must start alphanumeric (regex forbids a leading '-'); the trim already handles a leading dash.
        if (slug.Length > 63)
        {
            slug = slug[..63].Trim('-');
        }

        return slug.Length == 0 ? "app" : slug;
    }

    private static void Require(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"Ungültiger Stack-Name: '{name}'.", nameof(name));
        }
    }

    /// <summary>Writes the compose YAML (from stdin) and runs <c>compose up -d</c>. Output merged (2&gt;&amp;1).</summary>
    public static string Deploy(string dockerHead, string name)
    {
        Require(name);
        // name is validated (no shell metachars); the whole sh -c body is single-quoted and contains no
        // single quotes, so it reaches the remote sh verbatim. YAML arrives on stdin via `cat`.
        var script = "set -e; name=" + name + "; dir=\"$HOME/.matdock/stacks/$name\"; mkdir -p \"$dir\"; "
                   + "cat > \"$dir/docker-compose.yml\"; cd \"$dir\"; " + dockerHead + " compose -p \"$name\" up -d";
        return VolumeCommands.PathPrefix + "sh -c '" + script + "' 2>&1";
    }

    /// <summary><c>compose down</c> for the stack (from its dir). Output merged.</summary>
    public static string Down(string dockerHead, string name)
    {
        Require(name);
        var script = "set -e; name=" + name + "; dir=\"$HOME/.matdock/stacks/$name\"; cd \"$dir\"; " + dockerHead + " compose -p \"$name\" down";
        return VolumeCommands.PathPrefix + "sh -c '" + script + "' 2>&1";
    }
}
