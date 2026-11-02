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
    public static string Deploy(string dockerHead, string name) => Deploy(dockerHead, name, DeployOptions.None);

    /// <summary>
    /// Writes the stack's files from stdin, pulls newer images (not those of <paramref name="pinned"/> services), then runs
    /// <c>compose up -d</c>, which leaves a service alone whose image did not change.
    /// </summary>
    public static string Update(string dockerHead, string name, IReadOnlyCollection<string>? pinned = null)
        => Deploy(dockerHead, name, new DeployOptions { Pull = true }, pinned);

    /// <summary>
    /// Writes the stack's files from stdin (<see cref="FilesOnStdin"/>), then with <paramref name="options"/>: pulls (not the <paramref name="pinned"/>
    /// services), builds, and runs <c>compose up -d</c> with its flags. Output merged (2&gt;&amp;1).
    /// </summary>
    public static string Deploy(string dockerHead, string name, DeployOptions options, IReadOnlyCollection<string>? pinned = null)
    {
        Require(name);
        // name is validated (no shell metachars); the whole sh -c body is single-quoted and contains no
        // single quotes, so it reaches the remote sh verbatim. The files arrive on stdin.
        const string args = "-p \"$name\" $cf";
        var script = "set -e; name=" + name + "; dir=\"" + StacksDir + "/$name\"; mkdir -p \"$dir\"; "
                   + WriteFiles + "cd \"$dir\"; " + InlineFiles
                   + (options.Pull ? PullStep(dockerHead, args, pinned) : string.Empty)
                   + BuildStep(dockerHead, args, options)
                   + dockerHead + " compose " + args + Up(options);
        return VolumeCommands.PathPrefix + "sh -c '" + script + "' 2>&1";
    }

    /// <summary><c>compose build</c> when the options ask for it (with <c>--pull</c>, <c>--no-cache</c>), nothing otherwise.</summary>
    private static string BuildStep(string dockerHead, string composeArgs, DeployOptions options)
        => options.Builds
            ? dockerHead + " compose " + composeArgs + " build" + (options.PullBase ? " --pull" : string.Empty) + (options.NoCache ? " --no-cache" : string.Empty) + "; "
            : string.Empty;

    /// <summary><c> up -d</c> with the flags of the options.</summary>
    private static string Up(DeployOptions options)
        => " up -d" + (options.ForceRecreate ? " --force-recreate" : string.Empty) + (options.RemoveOrphans ? " --remove-orphans" : string.Empty);

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
        "IFS= read -r envb64 || true; IFS= read -r ovb64 || true; IFS= read -r xb64 || true; "
        + "if [ -n \"$envb64\" ]; then (umask 077; printf %s \"$envb64\" | base64 -d > \"$dir/.env\"); else rm -f \"$dir/.env\"; fi; "
        + "if [ -n \"$ovb64\" ]; then printf %s \"$ovb64\" | base64 -d > \"$dir/" + ImageOverrides.FileName + "\"; else rm -f \"$dir/" + ImageOverrides.FileName + "\"; fi; "
        + ExportVars + "rm -f \"$dir/" + EnvFilesName + "\"; "
        + "cat > \"$dir/docker-compose.yml\"; ";

    /// <summary>
    /// Exports the variables of <c>$xb64</c> (base64 of <c>NAME=value</c> lines, <see cref="StackSecrets.Exports"/>) into the environment of the script, so
    /// compose resolves <c>${NAME}</c> with them; nothing is written to a file. Globbing is off while the lines are split, so a value with <c>*</c> stays.
    /// No single quote: the fragment sits inside single-quoted script bodies.
    /// </summary>
    private const string ExportVars =
        "if [ -n \"$xb64\" ]; then xs=$(printf %s \"$xb64\" | base64 -d); set -f; nl=$(printf \"\\n_\"); nl=${nl%_}; oifs=$IFS; IFS=$nl; "
        + "for kv in $xs; do export \"$kv\"; done; IFS=$oifs; set +f; fi; ";

    /// <summary>Reads the exports line from stdin and exports it, when the flag argument <paramref name="flag"/> says it was sent.</summary>
    private static string ReadExports(string flag) => "if [ \"" + flag + "\" = x ]; then IFS= read -r xb64 || true; " + ExportVars + "fi; ";

    /// <summary>The flag argument that tells a script the exports line comes on stdin.</summary>
    private static string ExportsFlag(bool exportsOnStdin) => exportsOnStdin ? "x" : "-";

    /// <summary>The <c>--env-file</c> options of a Git stack (its <c>.env</c> and <see cref="Entities.Stack.GitEnvFile"/>), kept beside the files by a
    /// deploy so the later commands (restart, recreate of a service) read the same variables.</summary>
    public const string EnvFilesName = ".matdock-env-files";

    /// <summary>After <c>cd</c> into the stack dir: <c>$ef</c> holds the <c>--env-file</c> options a Git deploy kept, else it is empty.</summary>
    private const string EnvFiles = "ef=\"\"; if [ -f \"" + EnvFilesName + "\" ]; then ef=$(cat \"" + EnvFilesName + "\"); fi; ";

    /// <summary>The line <see cref="Lifecycle"/>, <see cref="Service"/> and <see cref="GitSync(string, string, string, DeployOptions, IReadOnlyCollection{string}?, string?, string?, string?, bool)"/>
    /// read first from stdin when they are told to: the exports in base64.</summary>
    public static byte[] ExportsLine(string? exports)
        => System.Text.Encoding.UTF8.GetBytes((string.IsNullOrEmpty(exports) ? string.Empty : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(exports))) + "\n");

    /// <summary>Whether an env file path of a repository is safe to name on the command line (it is split on spaces there).</summary>
    public static bool IsSafeEnvFilePath(string? path)
        => !string.IsNullOrEmpty(path) && path.Length <= 300 && !path.StartsWith('/') && !path.Split('/').Contains("..")
           && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/');

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
    /// (<see cref="ImageOverrides.Render"/>) as another (empty = none, a stale file is removed), the exports (<see cref="StackSecrets.Exports"/>: the
    /// secrets, never written to a file) as a third, then the compose file.</summary>
    public static byte[] FilesOnStdin(string composeYaml, string? envContent, string? overrideYaml = null, string? exports = null)
    {
        var env = string.IsNullOrEmpty(envContent)
            ? string.Empty
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(envContent.Replace("\r\n", "\n")));
        var x = string.IsNullOrEmpty(exports) ? string.Empty : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(exports));
        return System.Text.Encoding.UTF8.GetBytes(env + "\n" + ImageOverrides.Base64Of(overrideYaml) + "\n" + x + "\n" + composeYaml.Replace("\r\n", "\n"));
    }

    /// <summary><c>compose down</c> for the stack (stops and removes containers, with <c>--volumes</c> also its
    /// named volumes). Output merged.</summary>
    public static string Down(string dockerHead, string name, string composePath, bool removeVolumes = false, bool exportsOnStdin = false)
        => Lifecycle(dockerHead, name, composePath, removeVolumes ? "down --volumes" : "down", exportsOnStdin);

    /// <summary><c>compose restart</c> for the stack (restarts its containers without re-creating them). Output merged.</summary>
    public static string Restart(string dockerHead, string name, string composePath, bool exportsOnStdin = false)
        => Lifecycle(dockerHead, name, composePath, "restart", exportsOnStdin);

    /// <summary><c>compose pull</c> for the stack (fetches newer images, containers keep running). Output merged.</summary>
    public static string Pull(string dockerHead, string name, string composePath, bool exportsOnStdin = false)
        => Lifecycle(dockerHead, name, composePath, "pull", exportsOnStdin);

    /// <summary><c>compose stop</c> for the stack (stops containers, keeps them). Output merged.</summary>
    public static string Stop(string dockerHead, string name, string composePath, bool exportsOnStdin = false)
        => Lifecycle(dockerHead, name, composePath, "stop", exportsOnStdin);

    /// <summary><c>compose start</c> for the stack (starts previously-stopped containers). Output merged.</summary>
    public static string Start(string dockerHead, string name, string composePath, bool exportsOnStdin = false)
        => Lifecycle(dockerHead, name, composePath, "start", exportsOnStdin);

    /// <summary>What can be done to one service of a managed stack.</summary>
    public enum ServiceAction
    {
        Restart,
        Stop,
        Start,

        /// <summary>New containers for the service, whether or not its settings changed (<c>up -d --force-recreate --no-deps</c>).</summary>
        Recreate,

        /// <summary>Runs the service with a number of containers (<c>up -d --scale</c>).</summary>
        Scale,
    }

    /// <summary>
    /// One service of a managed stack: restart, stop, start, recreate or scale it, with the stack's compose file and its image overrides.
    /// $1 = stack, $2 = compose path, $3 = service, $4 = number of containers (all validated and passed as quoted arguments), $5 = whether the exports
    /// (the secrets) come on stdin. Recreate and scale need the files on the host (deployed from MatDock once); restart, stop and start fall back to the
    /// project label.
    /// </summary>
    public static string Service(string dockerHead, string name, string composePath, string service, ServiceAction action, int replicas = 1, bool exportsOnStdin = false)
    {
        Require(name);
        if (!IsValidServiceName(service))
        {
            throw new ArgumentException($"Invalid service name: '{service}'.", nameof(service));
        }
        if (action == ServiceAction.Scale && replicas is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(replicas), "Between 0 and 100 containers.");
        }

        var verb = action switch
        {
            ServiceAction.Restart => "restart \"$3\"",
            ServiceAction.Stop => "stop \"$3\"",
            ServiceAction.Start => "start \"$3\"",
            ServiceAction.Recreate => "up -d --force-recreate --no-deps \"$3\"",
            _ => "up -d --no-recreate --scale \"$3=$4\" \"$3\"",
        };
        var needsFiles = action is ServiceAction.Recreate or ServiceAction.Scale;
        var script = "set -e; " + ReadExports("$5") + "dir=\"" + StacksDir + "/$1\"; "
                   + "if [ -f \"$dir/$2\" ]; then cd \"$dir\"; " + OverrideFile + EnvFiles + dockerHead + " compose -p \"$1\" -f \"$2\" $ov $ef " + verb + "; "
                   + (needsFiles
                       ? "else echo \"The compose file of this stack is not on the host: deploy it from MatDock once.\" >&2; exit 3; fi"
                       : "else " + dockerHead + " compose -p \"$1\" " + verb + "; fi");
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath)
             + " " + VolumeFileCommands.ShellQuote(service) + " " + replicas.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + ExportsFlag(exportsOnStdin) + " 2>&1";
    }

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

    /// <summary>
    /// Lists the YAML files below a folder of the host (at most four levels deep) by mounting the folder read-only into the helper container, so it works for a
    /// local and a remote daemon alike. $1 = folder, $2 = helper image. One path per line, relative to the folder.
    /// </summary>
    public static string ScanFolder(string dockerHead, string folder, string helperImage)
    {
        var script = dockerHead + " run --rm -v \"$1\":/scan:ro \"$2\" find /scan -maxdepth 4 -type f -name \"*.y*ml\" 2>/dev/null";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(folder) + " " + VolumeFileCommands.ShellQuote(helperImage);
    }

    /// <summary>The compose project of every container (one per line), to tell which found compose files already run.</summary>
    public static string ComposeProjects(string dockerHead)
        => VolumeCommands.PathPrefix + dockerHead + " ps -a --format \"{{.Label \\\"com.docker.compose.project\\\"}}\" 2>/dev/null";

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

    private static string Lifecycle(string dockerHead, string name, string composePath, string verb, bool exportsOnStdin)
    {
        Require(name);
        // $1 = stack name (validated), $2 = compose file path (relative). Both passed as separate,
        // individually shell-quoted args so no user value is embedded in the single-quoted script body.
        // Without the files on the host (never deployed from here, or a lost directory) compose still finds
        // the project's containers by its label, so stop/start/down keep working.
        // MatDock's image overrides (a file next to the compose file) are part of the project whenever they exist.
        // $3 says whether the exports (the secrets) come on stdin.
        var script = "set -e; " + ReadExports("$3") + "dir=\"" + StacksDir + "/$1\"; "
                   + "if [ -f \"$dir/$2\" ]; then cd \"$dir\"; " + OverrideFile + EnvFiles + dockerHead + " compose -p \"$1\" -f \"$2\" $ov $ef " + verb + "; "
                   + "else " + dockerHead + " compose -p \"$1\" " + verb + "; fi";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath) + " " + ExportsFlag(exportsOnStdin) + " 2>&1";
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
        => GitSync(dockerHead, name, composePath, new DeployOptions { Pull = pull }, pinned, overrideYaml);

    /// <summary>
    /// The Git deploy with the deploy options: pull (not the <paramref name="pinned"/> services), build and the flags of <c>up</c>. With
    /// <paramref name="envFile"/> compose reads <paramref name="dotEnv"/> (when the repository has it) and then that file (both relative to the repository,
    /// <see cref="IsSafeEnvFilePath"/>); the options are kept in <see cref="EnvFilesName"/> for the later commands. With <paramref name="exportsOnStdin"/>
    /// the first line on stdin is the exports (<see cref="ExportsLine"/>), the tar follows. $4 = env file, $5 = .env, $6 = the exports flag.
    /// </summary>
    public static string GitSync(string dockerHead, string name, string composePath, DeployOptions options, IReadOnlyCollection<string>? pinned = null,
        string? overrideYaml = null, string? envFile = null, string? dotEnv = null, bool exportsOnStdin = false)
    {
        Require(name);
        if (!string.IsNullOrEmpty(envFile) && (!IsSafeEnvFilePath(envFile) || !IsSafeEnvFilePath(dotEnv)))
        {
            throw new ArgumentException($"Invalid env file path: '{envFile}'.", nameof(envFile));
        }

        const string args = "-p \"$1\" -f \"$2\" $ov $ef";
        // Optionally pull first so :latest images are refreshed before the (idempotent) up -d; pinned services are not pulled.
        var steps = (options.Pull ? PullStep(dockerHead, args, pinned) : string.Empty) + BuildStep(dockerHead, args, options);
        var script = "set -e; " + ReadExports("$6") + "dir=\"" + StacksDir + "/$1\"; mkdir -p \"$dir\"; "
                   + "tar -C \"$dir\" -xf -; cd \"$dir\"; "
                   + "if [ -n \"$3\" ]; then printf %s \"$3\" | base64 -d > \"" + ImageOverrides.FileName + "\"; else rm -f \"" + ImageOverrides.FileName + "\"; fi; "
                   + "ef=\"\"; if [ -n \"$4\" ]; then if [ -f \"$5\" ]; then ef=\"--env-file $5\"; fi; ef=\"$ef --env-file $4\"; fi; "
                   + "if [ -n \"$ef\" ]; then printf %s \"$ef\" > \"" + EnvFilesName + "\"; else rm -f \"" + EnvFilesName + "\"; fi; "
                   + OverrideFile + steps
                   + dockerHead + " compose " + args + Up(options);
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script)
             + " sh " + VolumeFileCommands.ShellQuote(name) + " " + VolumeFileCommands.ShellQuote(composePath)
             + " " + VolumeFileCommands.ShellQuote(ImageOverrides.Base64Of(overrideYaml))
             + " " + VolumeFileCommands.ShellQuote(envFile ?? string.Empty) + " " + VolumeFileCommands.ShellQuote(string.IsNullOrEmpty(envFile) ? string.Empty : dotEnv ?? string.Empty)
             + " " + ExportsFlag(exportsOnStdin) + " 2>&1";
    }
}
