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

    /// <summary>Drivers a user may create a network with (overlay needs Swarm; host/none can't be created).</summary>
    private static readonly HashSet<string> CreatableDrivers = new(StringComparer.Ordinal) { "bridge", "macvlan", "ipvlan" };

    public static bool IsValidDriver(string? driver) => driver is not null && CreatableDrivers.Contains(driver);

    /// <summary>Creates a network: <c>network create [--driver X] [--internal] [IPAM] name</c>. Name, driver and the addresses are validated.</summary>
    public static string Create(string name, string? driver, bool @internal, string dockerHead = "docker", NetworkIpam? ipam = null)
    {
        if (!IsValidNetworkRef(name))
        {
            throw new ArgumentException($"Invalid network name: '{name}'.", nameof(name));
        }
        if (!string.IsNullOrEmpty(driver) && !IsValidDriver(driver))
        {
            throw new ArgumentException($"Invalid network driver: '{driver}'.", nameof(driver));
        }
        if (ipam is not null && ipam.Problem(driver) is { } problem)
        {
            throw new ArgumentException(problem, nameof(ipam));
        }

        var d = string.IsNullOrEmpty(driver) ? string.Empty : " --driver " + driver;
        var intern = @internal ? " --internal" : string.Empty;
        return VolumeCommands.PathPrefix + dockerHead + " network create" + d + intern + (ipam?.Arguments() ?? string.Empty) + " '" + name + "'";
    }

    /// <summary>Attaches a container to a network (<c>network connect</c>), optionally with a fixed address and more names. Everything is validated.</summary>
    public static string Connect(string network, string container, string dockerHead = "docker", string? ip = null, IReadOnlyList<string>? aliases = null)
    {
        var plain = ConnectVerb("connect", network, container, dockerHead);
        if (string.IsNullOrEmpty(ip) && (aliases is null || aliases.Count == 0))
        {
            return plain;
        }

        if (!string.IsNullOrEmpty(ip) && !System.Net.IPAddress.TryParse(ip, out _))
        {
            throw new ArgumentException($"Invalid IP address: '{ip}'.", nameof(ip));
        }
        if (aliases?.FirstOrDefault(a => !AliasRegex().IsMatch(a)) is { } bad)
        {
            throw new ArgumentException($"Invalid alias: '{bad}'.", nameof(aliases));
        }

        var options = (string.IsNullOrEmpty(ip) ? string.Empty : (ip.Contains(':') ? " --ip6 '" : " --ip '") + ip + "'")
                      + string.Concat((aliases ?? []).Select(a => " --alias '" + a + "'"));
        return plain.Replace(" network connect ", " network connect" + options + " ", StringComparison.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\z")]
    private static partial System.Text.RegularExpressions.Regex AliasRegex();

    public static bool IsValidAlias(string? alias) => alias is not null && AliasRegex().IsMatch(alias);

    /// <summary>Detaches a container from a network (<c>network disconnect</c>). Both refs are validated.</summary>
    public static string Disconnect(string network, string container, string dockerHead = "docker")
        => ConnectVerb("disconnect", network, container, dockerHead);

    private static string ConnectVerb(string verb, string network, string container, string dockerHead)
    {
        if (!IsValidNetworkRef(network))
        {
            throw new ArgumentException($"Invalid network name/id: '{network}'.", nameof(network));
        }
        if (!IsValidNetworkRef(container)) // container id/name shares the network ref charset
        {
            throw new ArgumentException($"Invalid container id/name: '{container}'.", nameof(container));
        }

        return VolumeCommands.PathPrefix + $"{dockerHead} network {verb} '{network}' '{container}'";
    }
}
