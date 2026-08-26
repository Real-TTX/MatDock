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
            throw new ArgumentException($"Ungültiger Volume-Name: '{volume}'.", nameof(volume));
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
            throw new ArgumentException($"Ungültiger Projektname: '{project}'.", nameof(project));
        }

        return VolumeCommands.PathPrefix + dockerHead + " ps -a --filter label=com.docker.compose.project='" + project + "' --format '{{json .}}'";
    }

    /// <summary>One container by id (as a docker ps JSON line).</summary>
    public static string Get(string dockerHead, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Ungültige Container-ID: '{id}'.", nameof(id));
        }

        return VolumeCommands.PathPrefix + dockerHead + " ps -a --filter id='" + id + "' --format '{{json .}}'";
    }

    /// <summary>The mounts (volumes/binds) of one container as a JSON array.</summary>
    public static string InspectMounts(string dockerHead, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Ungültige Container-ID: '{id}'.", nameof(id));
        }

        return VolumeCommands.PathPrefix + dockerHead + " inspect --format '{{json .Mounts}}' '" + id + "'";
    }

    public static string Action(string dockerHead, ContainerAction action, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Ungültige Container-ID: '{id}'.", nameof(id));
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
            throw new ArgumentException($"Ungültige Container-ID: '{id}'.", nameof(id));
        }

        var lines = Math.Clamp(tail, 1, 5000);
        // Logs may go to stderr; merge so we capture everything.
        return VolumeCommands.PathPrefix + $"{dockerHead} logs --tail {lines} '{id}' 2>&1";
    }
}
