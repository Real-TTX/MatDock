using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Containers;

/// <summary>Injection-safe builders for container commands (ids/names are validated first).</summary>
public static partial class ContainerCommands
{
    // Container id or name: start alphanumeric, then [a-zA-Z0-9_.-].
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdRegex().IsMatch(id);

    public static string List(string dockerHead)
        => VolumeCommands.PathPrefix + dockerHead + " ps -a --format '{{json .}}'";

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
