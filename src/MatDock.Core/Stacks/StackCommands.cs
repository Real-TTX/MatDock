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
            throw new ArgumentException($"Invalid stack name: '{name}'.", nameof(name));
        }
    }

    /// <summary>
    /// <c>docker login</c> for a registry. The token/password is read from stdin (<c>--password-stdin</c>)
    /// so it never appears on the command line or in the host's process list. Host and user are
    /// individually shell-quoted. Output merged (2&gt;&amp;1).
    /// </summary>
    public static string Login(string dockerHead, string host, string user)
    {
        var script = dockerHead + " login " + VolumeFileCommands.ShellQuote(host)
                   + " -u " + VolumeFileCommands.ShellQuote(user) + " --password-stdin";
        return VolumeCommands.PathPrefix + script + " 2>&1";
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

    /// <summary>Writes the compose YAML (from stdin), pulls newer images, then runs <c>compose up -d</c>.</summary>
    public static string Update(string dockerHead, string name)
    {
        Require(name);
        var script = "set -e; name=" + name + "; dir=\"$HOME/.matdock/stacks/$name\"; mkdir -p \"$dir\"; "
                   + "cat > \"$dir/docker-compose.yml\"; cd \"$dir\"; "
                   + dockerHead + " compose -p \"$name\" pull; " + dockerHead + " compose -p \"$name\" up -d";
        return VolumeCommands.PathPrefix + "sh -c '" + script + "' 2>&1";
    }

    /// <summary><c>compose down</c> for the stack (stops and removes containers). Output merged.</summary>
    public static string Down(string dockerHead, string name, string composePath)
        => Lifecycle(dockerHead, name, composePath, "down");

    /// <summary><c>compose stop</c> for the stack (stops containers, keeps them). Output merged.</summary>
    public static string Stop(string dockerHead, string name, string composePath)
        => Lifecycle(dockerHead, name, composePath, "stop");

    /// <summary><c>compose start</c> for the stack (starts previously-stopped containers). Output merged.</summary>
    public static string Start(string dockerHead, string name, string composePath)
        => Lifecycle(dockerHead, name, composePath, "start");

    // ---- Discovered (external) stacks: operate by compose project label, no compose file needed ----

    /// <summary>Starts all containers of a discovered compose project (by label).</summary>
    public static string ProjectStart(string dockerHead, string project) => ProjectDocker(dockerHead, project, allContainers: true, "start");

    /// <summary>Stops the running containers of a discovered compose project (by label).</summary>
    public static string ProjectStop(string dockerHead, string project) => ProjectDocker(dockerHead, project, allContainers: false, "stop");

    /// <summary>Restarts the containers of a discovered compose project (by label).</summary>
    public static string ProjectRestart(string dockerHead, string project) => ProjectDocker(dockerHead, project, allContainers: true, "restart");

    private static string ProjectDocker(string dockerHead, string project, bool allContainers, string verb)
    {
        Require(project); // a compose project name follows the same rules as a stack name
        var psFlag = allContainers ? "-aq" : "-q";
        // $1 = project (validated + shell-quoted arg). No inner single quotes so the whole body quotes cleanly.
        var script = "ids=$(" + dockerHead + " ps " + psFlag + " --filter label=com.docker.compose.project=\"$1\"); "
                   + "if [ -z \"$ids\" ]; then echo \"No matching containers.\"; else " + dockerHead + " " + verb + " $ids; fi";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(project) + " 2>&1";
    }

    private static string Lifecycle(string dockerHead, string name, string composePath, string verb)
    {
        Require(name);
        // $1 = stack name (validated), $2 = compose file path (relative). Both passed as separate,
        // individually shell-quoted args so no user value is embedded in the single-quoted script body.
        var script = "set -e; dir=\"$HOME/.matdock/stacks/$1\"; cd \"$dir\"; "
                   + dockerHead + " compose -p \"$1\" -f \"$2\" " + verb;
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath) + " 2>&1";
    }

    /// <summary>
    /// Extracts a repo tarball (from stdin) into the stack dir and runs <c>compose up -d</c> with the given
    /// compose file. Files are overlaid (never wiped) so bind-mounted runtime data survives a redeploy.
    /// $1 = stack name, $2 = compose path (both shell-quoted args, not embedded).
    /// </summary>
    public static string GitSync(string dockerHead, string name, string composePath, bool pull = false)
    {
        Require(name);
        // Optionally pull first so :latest images are refreshed before the (idempotent) up -d.
        var pullStep = pull ? dockerHead + " compose -p \"$1\" -f \"$2\" pull; " : string.Empty;
        var script = "set -e; dir=\"$HOME/.matdock/stacks/$1\"; mkdir -p \"$dir\"; "
                   + "tar -C \"$dir\" -xf -; cd \"$dir\"; " + pullStep
                   + dockerHead + " compose -p \"$1\" -f \"$2\" up -d";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath) + " 2>&1";
    }
}
