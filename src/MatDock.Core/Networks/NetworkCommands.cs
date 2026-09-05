using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Networks;

/// <summary>
/// Pure builders for the remote Docker commands used by network operations. Network names/ids are
/// strictly validated first, so the strings can be embedded in the SSH command line without risking
/// shell injection. Reuses <see cref="VolumeCommands.PathPrefix"/> and <see cref="VolumeCommands.DockerHead"/>.
/// </summary>
public static partial class NetworkCommands
{
    // Docker network names follow the same rule as volumes (start alphanumeric, then [a-zA-Z0-9_.-]);
    // ids are hex, which the same character class also accepts. \z (not $) rejects a trailing newline.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}\z")]
    private static partial Regex NetworkRefRegex();

    /// <summary>A valid network name or id (safe to embed in a shell command line after quoting).</summary>
    public static bool IsValidNetworkRef(string? nameOrId)
        => !string.IsNullOrEmpty(nameOrId) && NetworkRefRegex().IsMatch(nameOrId);

    // NOTE: the Go-template braces are built by concatenation (not interpolation) so "{{json .}}"
    // survives — an interpolated string would collapse it to "{json .}" and break the command.
    public static string List(string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " network ls --format '{{json .}}'";

    /// <summary>Names (one per line) of networks not used by any container — Docker's "dangling" filter.</summary>
    public static string ListDangling(string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " network ls -q --filter dangling=true --format '{{.Name}}'";

    public static string InspectJson(string nameOrId, string dockerHead = "docker")
    {
        if (!IsValidNetworkRef(nameOrId))
        {
            throw new ArgumentException($"Invalid network name/id: '{nameOrId}'.", nameof(nameOrId));
        }

        return VolumeCommands.PathPrefix + dockerHead + " network inspect '" + nameOrId + "' --format '{{json .}}'";
    }

    public static string Remove(string nameOrId, string dockerHead = "docker")
    {
        if (!IsValidNetworkRef(nameOrId))
        {
            throw new ArgumentException($"Invalid network name/id: '{nameOrId}'.", nameof(nameOrId));
        }

        return VolumeCommands.PathPrefix + $"{dockerHead} network rm '{nameOrId}'";
    }

    /// <summary>Removes all unused (custom) networks. Predefined bridge/host/none are never touched.</summary>
    public static string Prune(string dockerHead = "docker")
        => VolumeCommands.PathPrefix + dockerHead + " network prune -f";
}
