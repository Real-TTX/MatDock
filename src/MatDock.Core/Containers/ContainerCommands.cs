using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Containers;

/// <summary>Injection-safe builders for container commands (ids/names are validated first).</summary>
public static partial class ContainerCommands
{
    // Container id or name: start alphanumeric, then [a-zA-Z0-9_.-].
    // \z (not $) so a trailing newline is rejected — $ would match just before a final \n.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdRegex().IsMatch(id);

    public static string List(string dockerHead)
        => VolumeCommands.PathPrefix + dockerHead + " ps -a --format '{{json .}}'";

    /// <summary>Per-container live usage (running containers only). Concatenated to keep the Go template braces intact.</summary>
    public static string Stats(string dockerHead)
        => VolumeCommands.PathPrefix + dockerHead + " stats --no-stream --format '{{json .}}'";

    /// <summary>Containers that mount the given (validated) volume. <paramref name="runningOnly"/> false = include stopped.</summary>
    public static string ListByVolume(string dockerHead, string volume, bool runningOnly)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            throw new ArgumentException($"Invalid volume name: '{volume}'.", nameof(volume));
        }

        var all = runningOnly ? string.Empty : "-a ";
        // volume is validated (no shell metacharacters) and single-quoted; braces built by concatenation.
        return VolumeCommands.PathPrefix + dockerHead + " ps " + all + "--filter volume='" + volume + "' --format '{{json .}}'";
    }

    /// <summary>All containers of a compose project (stack). Project name is validated like a container id.</summary>
    public static string ListByProject(string dockerHead, string project)
    {
        if (!IsValidId(project))
        {
            throw new ArgumentException($"Invalid project name: '{project}'.", nameof(project));
        }

        return VolumeCommands.PathPrefix + dockerHead + " ps -a --filter label=com.docker.compose.project='" + project + "' --format '{{json .}}'";
    }

    /// <summary>One container by id (as a docker ps JSON line).</summary>
    public static string Get(string dockerHead, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Invalid container ID: '{id}'.", nameof(id));
        }

        return VolumeCommands.PathPrefix + dockerHead + " ps -a --filter id='" + id + "' --format '{{json .}}'";
    }

    /// <summary>The mounts (volumes/binds) of one container as a JSON array.</summary>
    public static string InspectMounts(string dockerHead, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Invalid container ID: '{id}'.", nameof(id));
        }

        return VolumeCommands.PathPrefix + dockerHead + " inspect --format '{{json .Mounts}}' '" + id + "'";
    }

    public static string Action(string dockerHead, ContainerAction action, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Invalid container ID: '{id}'.", nameof(id));
        }

        var verb = action switch
        {
            ContainerAction.Start => "start",
            ContainerAction.Stop => "stop",
            ContainerAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        return VolumeCommands.PathPrefix + $"{dockerHead} {verb} '{id}'";
    }

    public static string Logs(string dockerHead, string id, int tail)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Invalid container ID: '{id}'.", nameof(id));
        }

        var lines = Math.Clamp(tail, 1, 5000);
        // Logs may go to stderr; merge so we capture everything.
        return VolumeCommands.PathPrefix + $"{dockerHead} logs --tail {lines} '{id}' 2>&1";
    }

    /// <summary>Follows a container's logs live (<c>-f</c>). Streams until the channel/process is closed.</summary>
    public static string LogsFollow(string dockerHead, string id, int tail, bool timestamps = false)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Invalid container ID: '{id}'.", nameof(id));
        }

        var lines = Math.Clamp(tail, 0, 5000); // 0 = only new lines (used on resume)
        var t = timestamps ? " -t" : string.Empty;
        return VolumeCommands.PathPrefix + $"{dockerHead} logs -f{t} --tail {lines} '{id}' 2>&1";
    }

    /// <summary>Follows the logs of every container of a compose project (stack), each line prefixed with its service.
    /// Compose finds the containers by the project label, so no compose file is needed (discovered stacks work too).</summary>
    public static string StackLogsFollow(string dockerHead, string project, int tail, bool timestamps = false)
    {
        if (!IsValidId(project))
        {
            throw new ArgumentException($"Invalid project name: '{project}'.", nameof(project));
        }

        var lines = Math.Clamp(tail, 0, 5000);
        var t = timestamps ? " -t" : string.Empty;
        return VolumeCommands.PathPrefix + $"{dockerHead} compose -p '{project}' logs -f --no-color{t} --tail {lines} 2>&1";
    }

    /// <summary>
    /// Service, image reference and local image id of the given containers, one line each (<c>web nginx:latest sha256:…</c>), so an update can
    /// put the old image back under its tag.
    /// </summary>
    public static string ImagesOf(string dockerHead, IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0 || ids.Any(id => !IsValidId(id)))
        {
            throw new ArgumentException("Invalid container ID.", nameof(ids));
        }

        return VolumeCommands.PathPrefix + dockerHead + " inspect --format "
               + VolumeFileCommands.ShellQuote("{{index .Config.Labels \"com.docker.compose.service\"}} {{.Config.Image}} {{.Image}}")
               + " " + string.Join(' ', ids.Select(id => "'" + id + "'"));
    }

    /// <summary>Reads <see cref="ImagesOf"/>: (service, image reference, image id) per line.</summary>
    public static IReadOnlyList<(string Service, string Reference, string ImageId)> ParseImagesOf(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 3 && p[2].StartsWith("sha256:", StringComparison.Ordinal))
            .Select(p => (p[0], p[1], p[2]))
            .ToList();

    /// <summary>Starts bash in the container when it has one, otherwise sh.</summary>
    public const string ShellPicker = "if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi";

    /// <summary>An interactive shell in the container (<c>docker exec -it</c>); needs a terminal around it.</summary>
    public static string ExecShell(string dockerHead, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Invalid container ID: '{id}'.", nameof(id));
        }

        return dockerHead + " exec -it '" + id + "' sh -c " + VolumeFileCommands.ShellQuote(ShellPicker);
    }

    /// <summary>
    /// The same shell for the Docker socket of MatDock's own host: <c>script</c> (util-linux) gives it the terminal that
    /// <c>docker exec -it</c> needs, sized to the browser's window when it opens.
    /// </summary>
    public static string LocalExecShell(string dockerHead, string id, int cols, int rows)
    {
        var inner = "stty cols " + Math.Clamp(cols, 1, 500) + " rows " + Math.Clamp(rows, 1, 200) + " 2>/dev/null; exec "
                    + ExecShell(dockerHead, id);
        return VolumeCommands.PathPrefix + "exec script -qfec " + VolumeFileCommands.ShellQuote(inner) + " /dev/null";
    }

    /// <summary>
    /// A non-streaming snapshot of container lifecycle events from the last <paramref name="sinceHours"/> hours.
    /// Both <c>--since</c> and <c>--until</c> are set so <c>docker events</c> returns the window and exits
    /// (with only <c>--since</c> it would keep streaming live and block the SSH command).
    /// </summary>
    public static string Events(string dockerHead, int sinceHours)
    {
        var hours = Math.Clamp(sinceHours, 1, 168);
        return VolumeCommands.PathPrefix + dockerHead +
               " events --since " + hours + "h --until 0s --filter type=container --format '{{json .}}'";
    }
}
