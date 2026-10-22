using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Stacks;

/// <summary>
/// Builders for the remote <c>docker compose</c> commands of a managed stack. The stack name is a
/// strictly validated compose project name, so it is safe to embed; the compose YAML is delivered via
/// stdin (never interpolated into the command). Files live in <c>$MATDOCK_STACKS_DIR/&lt;name&gt;</c>, which only the
/// local session sets (to <c>&lt;data&gt;/stacks</c>, so the files survive a re-creation of the MatDock container);
/// on an SSH host it falls back to <c>$HOME/.matdock/stacks/&lt;name&gt;</c>.
/// </summary>
public static partial class StackCommands
{
    /// <summary>Name of the environment variable that overrides the stacks base directory on the host.</summary>
    public const string StacksDirVariable = "MATDOCK_STACKS_DIR";

    /// <summary>Shell expression for the stacks base directory (no single quotes, safe inside a single-quoted body).</summary>
    public const string StacksDir = "${" + StacksDirVariable + ":-$HOME/.matdock/stacks}";

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

    /// <summary>
    /// Writes the stack's files from stdin (<see cref="FilesOnStdin"/>) and runs <c>compose up -d</c>. Output merged (2&gt;&amp;1).
    /// </summary>
    public static string Deploy(string dockerHead, string name)
    {
        Require(name);
        // name is validated (no shell metachars); the whole sh -c body is single-quoted and contains no
        // single quotes, so it reaches the remote sh verbatim. The files arrive on stdin.
        var script = "set -e; name=" + name + "; dir=\"" + StacksDir + "/$name\"; mkdir -p \"$dir\"; "
                   + WriteFiles + "cd \"$dir\"; " + InlineFiles + dockerHead + " compose -p \"$name\" $cf up -d";
        return VolumeCommands.PathPrefix + "sh -c '" + script + "' 2>&1";
    }

    /// <summary>
    /// Writes the stack's files from stdin, pulls newer images (not those of <paramref name="pinned"/> services), then runs
    /// <c>compose up -d</c>, which leaves a service alone whose image did not change.
    /// </summary>
    public static string Update(string dockerHead, string name, IReadOnlyCollection<string>? pinned = null)
    {
        Require(name);
        var script = "set -e; name=" + name + "; dir=\"" + StacksDir + "/$name\"; mkdir -p \"$dir\"; "
                   + WriteFiles + "cd \"$dir\"; " + InlineFiles
                   + PullStep(dockerHead, "-p \"$name\" $cf", pinned) + dockerHead + " compose -p \"$name\" $cf up -d";
        return VolumeCommands.PathPrefix + "sh -c '" + script + "' 2>&1";
    }

    // A compose service name: what compose accepts for a key, kept to a safe set (no quotes, spaces or shell characters).
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}\z")]
    private static partial Regex ServiceRegex();

    public static bool IsValidServiceName(string? service) => !string.IsNullOrEmpty(service) && ServiceRegex().IsMatch(service);

    /// <summary>
    /// <c>compose pull</c> of every service except the pinned ones. Without pins it is a plain pull; with pins the services come from
    /// <c>compose config --services</c> and the pinned are left out (nothing is pulled when all are pinned). The names are validated and
    /// contain no quote, so the fragment fits inside the single-quoted script bodies.
    /// </summary>
    private static string PullStep(string dockerHead, string composeArgs, IReadOnlyCollection<string>? pinned)
    {
        if (pinned is null || pinned.Count == 0)
        {
            return dockerHead + " compose " + composeArgs + " pull; ";
        }

        if (pinned.Any(p => !IsValidServiceName(p)))
        {
            throw new ArgumentException("Invalid pinned service name.", nameof(pinned));
        }

        return "pins=\" " + string.Join(' ', pinned) + " \"; svcs=\"\"; "
               + "for s in $(" + dockerHead + " compose " + composeArgs + " config --services); do "
               + "case \"$pins\" in *\" $s \"*) ;; *) svcs=\"$svcs $s\";; esac; done; "
               + "if [ -n \"$svcs\" ]; then " + dockerHead + " compose " + composeArgs + " pull $svcs; fi; ";
    }

    /// <summary>
    /// Reads what <see cref="FilesOnStdin"/> sends: the first line is the <c>.env</c> in base64 (empty = no <c>.env</c>, a stale one is
    /// removed so cleared variables take effect), the rest is the compose file. The <c>.env</c> holds secrets, so it never appears on
    /// the command line (where <c>ps</c> on the host would show it) and is readable by its owner only.
    /// </summary>
    private const string WriteFiles =
        "IFS= read -r envb64 || true; IFS= read -r ovb64 || true; "
        + "if [ -n \"$envb64\" ]; then (umask 077; printf %s \"$envb64\" | base64 -d > \"$dir/.env\"); else rm -f \"$dir/.env\"; fi; "
        + "if [ -n \"$ovb64\" ]; then printf %s \"$ovb64\" | base64 -d > \"$dir/" + ImageOverrides.FileName + "\"; else rm -f \"$dir/" + ImageOverrides.FileName + "\"; fi; "
        + "cat > \"$dir/docker-compose.yml\"; ";

    /// <summary>After <see cref="WriteFiles"/> and <c>cd</c>: the compose files to name when MatDock overrides images (<c>$cf</c>), nothing otherwise.</summary>
    private const string InlineFiles =
        "cf=\"\"; if [ -f \"" + ImageOverrides.FileName + "\" ]; then cf=\"-f docker-compose.yml -f " + ImageOverrides.FileName + "\"; fi; ";

    /// <summary>
    /// <c>docker compose config</c> of the files on stdin (<see cref="FilesOnStdin"/>) in a temporary directory on the host, which is
    /// removed afterwards: the check of the compose file with its variables by compose itself, without deploying anything.
    /// </summary>
    public static string CheckConfig(string dockerHead)
    {
        var script = "set -e; dir=$(mktemp -d); trap 'rm -rf \"$dir\"' EXIT; " + WriteFiles
                     + "cd \"$dir\"; " + dockerHead + " compose -p matdock-check config --quiet; echo 'The compose file is valid.'";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script) + " 2>&1";
    }

    /// <summary>What <see cref="Deploy"/> and <see cref="Update"/> read from stdin: the <c>.env</c> as one base64 line, the image overrides
    /// (<see cref="ImageOverrides.Render"/>) as another (empty = none, a stale file is removed), then the compose file.</summary>
    public static byte[] FilesOnStdin(string composeYaml, string? envContent, string? overrideYaml = null)
    {
        var env = string.IsNullOrEmpty(envContent)
            ? string.Empty
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(envContent.Replace("\r\n", "\n")));
        return System.Text.Encoding.UTF8.GetBytes(env + "\n" + ImageOverrides.Base64Of(overrideYaml) + "\n" + composeYaml.Replace("\r\n", "\n"));
    }

    /// <summary><c>compose down</c> for the stack (stops and removes containers, with <c>--volumes</c> also its
    /// named volumes). Output merged.</summary>
    public static string Down(string dockerHead, string name, string composePath, bool removeVolumes = false)
        => Lifecycle(dockerHead, name, composePath, removeVolumes ? "down --volumes" : "down");

    /// <summary><c>compose restart</c> for the stack (restarts its containers without re-creating them). Output merged.</summary>
    public static string Restart(string dockerHead, string name, string composePath)
        => Lifecycle(dockerHead, name, composePath, "restart");

    /// <summary><c>compose pull</c> for the stack (fetches newer images, containers keep running). Output merged.</summary>
    public static string Pull(string dockerHead, string name, string composePath)
        => Lifecycle(dockerHead, name, composePath, "pull");

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

    /// <summary>
    /// Emits the compose labels (JSON) of the first container of a discovered project, so MatDock can read
    /// the project's compose-file path(s) and working dir and adopt it. Exit 3 when the project has no
    /// container. $1 = project (validated, shell-quoted arg).
    /// </summary>
    public static string AdoptInspect(string dockerHead, string project)
    {
        Require(project);
        var script = "id=$(" + dockerHead + " ps -aq --filter label=com.docker.compose.project=\"$1\" | head -n1); "
                   + "if [ -z \"$id\" ]; then exit 3; fi; "
                   + dockerHead + " inspect \"$id\" --format \"{{json .Config.Labels}}\"";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(project);
    }

    /// <summary>
    /// Reads a file from the host by bind-mounting its directory read-only into a tiny helper container and
    /// cat-ing it — works for both local and remote daemons. $1 = directory, $2 = file name, $3 = helper image
    /// (all shell-quoted args, never interpolated). Pull/mount noise is dropped so stdout is the file only.
    /// </summary>
    public static string ReadProjectFile(string dockerHead, string dir, string fileName, string helperImage)
    {
        var script = dockerHead + " run --rm -v \"$1\":/src:ro \"$3\" cat \"/src/$2\" 2>/dev/null";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(dir) + " " + VolumeFileCommands.ShellQuote(fileName)
             + " " + VolumeFileCommands.ShellQuote(helperImage);
    }

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
        // Without the files on the host (never deployed from here, or a lost directory) compose still finds
        // the project's containers by its label, so stop/start/down keep working.
        // MatDock's image overrides (a file next to the compose file) are part of the project whenever they exist.
        var script = "set -e; dir=\"" + StacksDir + "/$1\"; "
                   + "if [ -f \"$dir/$2\" ]; then cd \"$dir\"; " + OverrideFile + dockerHead + " compose -p \"$1\" -f \"$2\" $ov " + verb + "; "
                   + "else " + dockerHead + " compose -p \"$1\" " + verb + "; fi";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath) + " 2>&1";
    }

    /// <summary>After <c>cd</c> into the stack dir: <c>$ov</c> names the image overrides when that file exists, else it is empty.</summary>
    private const string OverrideFile =
        "ov=\"\"; if [ -f \"" + ImageOverrides.FileName + "\" ]; then ov=\"-f " + ImageOverrides.FileName + "\"; fi; ";

    /// <summary>
    /// Extracts a repo tarball (from stdin) into the stack dir and runs <c>compose up -d</c> with the given
    /// compose file. Files are overlaid (never wiped) so bind-mounted runtime data survives a redeploy.
    /// $1 = stack name, $2 = compose path, $3 = the image overrides (<see cref="ImageOverrides.Base64Of"/>, empty = none; a stale file is removed),
    /// all shell-quoted args, not embedded.
    /// </summary>
    public static string GitSync(string dockerHead, string name, string composePath, bool pull = false, IReadOnlyCollection<string>? pinned = null, string? overrideYaml = null)
    {
        Require(name);
        // Optionally pull first so :latest images are refreshed before the (idempotent) up -d; pinned services are not pulled.
        var pullStep = pull ? PullStep(dockerHead, "-p \"$1\" -f \"$2\" $ov", pinned) : string.Empty;
        var script = "set -e; dir=\"" + StacksDir + "/$1\"; mkdir -p \"$dir\"; "
                   + "tar -C \"$dir\" -xf -; cd \"$dir\"; "
                   + "if [ -n \"$3\" ]; then printf %s \"$3\" | base64 -d > \"" + ImageOverrides.FileName + "\"; else rm -f \"" + ImageOverrides.FileName + "\"; fi; "
                   + OverrideFile + pullStep
                   + dockerHead + " compose -p \"$1\" -f \"$2\" $ov up -d";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath)
             + " " + VolumeFileCommands.ShellQuote(ImageOverrides.Base64Of(overrideYaml)) + " 2>&1";
    }
}
