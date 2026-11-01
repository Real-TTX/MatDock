using System.ComponentModel;
using MatDock.Core.Containers;
using MatDock.Core.Environments;
using MatDock.Core.Networks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>Images, volumes and networks of an environment.</summary>
[McpServerToolType]
public static class ResourceTools
{
    private const string EnvironmentHelp = "The environment: id or name (may be left out when there is only one).";

    [McpServerTool(Name = "list_images", ReadOnly = true), Description("The images of an environment: reference, id, size, age, whether it is dangling, and the containers that use it.")]
    public static async Task<object> ListImages(EnvironmentService environments, IEnvironmentConnectionService connection, ContainerService containers,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("Only those whose reference contains this text.")] string? search = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var images = await connection.ListImagesAsync(environments.BuildSettings(env), ct);
        var used = await containers.ListAsync(env, ct, includeStats: false);
        return images
            .Where(i => string.IsNullOrWhiteSpace(search) || i.Reference.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(i => new
            {
                i.Reference, i.Id, i.Size, created = i.CreatedSince, i.Dangling,
                usedBy = used.Where(c => c.Image == i.Reference || c.Image == i.Id).Select(c => c.Name),
            });
    }

    [McpServerTool(Name = "pull_image", ReadOnly = false), Description("Pulls an image onto an environment (signed in to its registry when MatDock knows one for it).")]
    public static async Task<string> PullImage(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description("The image, e.g. redis:7.")] string reference, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, message) = await connection.PullImageAsync(environments.BuildSettings(env), reference.Trim(), ct);
        return ok ? $"Pulled {reference} onto {env.Name}." : throw new McpException(message);
    }

    [McpServerTool(Name = "remove_image", ReadOnly = false, Destructive = true), Description("Removes an image. Docker refuses one that a container uses (with force also one that only stopped containers use).")]
    public static async Task<string> RemoveImage(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description("The image reference or id.")] string reference, [Description(EnvironmentHelp)] string? environment = null,
        [Description("Also when stopped containers use it.")] bool force = false, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, message) = await connection.RemoveImageAsync(environments.BuildSettings(env), reference.Trim(), force, ct);
        return ok ? $"Removed {reference} from {env.Name}." : throw new McpException(message);
    }

    [McpServerTool(Name = "list_volumes", ReadOnly = true), Description("The volumes of an environment: name, driver, mount point and whether a container uses it.")]
    public static async Task<object> ListVolumes(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (volumes, unused) = await connection.ListVolumesWithUsageAsync(environments.BuildSettings(env), ct);
        return volumes.Select(v => new { v.Name, v.Driver, v.Mountpoint, inUse = !unused.Contains(v.Name) });
    }

    [McpServerTool(Name = "create_volume", ReadOnly = false), Description("Creates a volume, optionally with a driver and driver options (an NFS or CIFS share: driver local with type, o and device).")]
    public static async Task<string> CreateVolume(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description("The name of the volume.")] string name, [Description(EnvironmentHelp)] string? environment = null,
        [Description("The driver (default local).")] string? driver = null,
        [Description("Driver options as key=value, e.g. type=nfs, o=addr=10.0.0.5,rw, device=:/export/data.")] string[]? options = null,
        CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var pairs = new List<(string Key, string Value)>();
        foreach (var option in options ?? [])
        {
            var cut = option.IndexOf('=');
            if (cut <= 0)
            {
                throw new McpException($"'{option}' is no key=value option.");
            }

            pairs.Add((option[..cut].Trim(), option[(cut + 1)..].Trim()));
        }

        var (ok, message) = await connection.CreateVolumeAsync(environments.BuildSettings(env), name.Trim(), string.IsNullOrWhiteSpace(driver) ? null : driver.Trim(), pairs, ct);
        return ok ? $"Volume {name} created on {env.Name}." : throw new McpException(message);
    }

    [McpServerTool(Name = "remove_volume", ReadOnly = false, Destructive = true), Description("Removes a volume and all data in it. Refused while a container uses it. Cannot be undone.")]
    public static async Task<string> RemoveVolume(EnvironmentService environments, IEnvironmentConnectionService connection, ContainerService containers,
        [Description("The name of the volume.")] string name, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var users = await containers.ListByVolumeAsync(env, name.Trim(), ct);
        if (users.Count > 0)
        {
            throw new McpException($"Volume '{name}' is used by {string.Join(", ", users.Select(c => c.Name))}.");
        }

        var result = await connection.RemoveVolumesAsync(environments.BuildSettings(env), [name.Trim()], ct);
        return result.Success ? $"Volume {name} removed from {env.Name}." : throw new McpException(result.Detail);
    }

    [McpServerTool(Name = "list_networks", ReadOnly = true), Description("The networks of an environment: name, id, driver, scope, internal, predefined (bridge, host, none) and whether a container uses it.")]
    public static async Task<object> ListNetworks(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (networks, unused) = await connection.ListNetworksWithUsageAsync(environments.BuildSettings(env), ct);
        return networks.Select(n => new { n.Name, n.Id, n.Driver, n.Scope, n.Internal, predefined = n.IsPredefined, inUse = !unused.Contains(n.Name) });
    }

    [McpServerTool(Name = "create_network", ReadOnly = false), Description("Creates a network (bridge, macvlan, ipvlan, overlay), optionally internal and with its own addresses.")]
    public static async Task<string> CreateNetwork(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description("The name of the network.")] string name, [Description(EnvironmentHelp)] string? environment = null,
        [Description("The driver (default bridge).")] string? driver = null,
        [Description("No way out to other networks.")] bool @internal = false,
        [Description("An IPv4 subnet, e.g. 172.30.0.0/24.")] string? subnet = null,
        [Description("The gateway in the subnet.")] string? gateway = null,
        [Description("The range addresses are given from, inside the subnet.")] string? ipRange = null,
        [Description("An IPv6 subnet (switches IPv6 on).")] string? ipv6Subnet = null,
        [Description("The parent interface of a macvlan or ipvlan network, e.g. eth0.")] string? parent = null,
        CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var ipam = new NetworkIpam(Empty(subnet), Empty(gateway), Empty(ipRange), !string.IsNullOrWhiteSpace(ipv6Subnet), Empty(ipv6Subnet), Empty(parent));
        var networkDriver = Empty(driver) ?? "bridge";
        if (ipam.Problem(networkDriver) is { } problem)
        {
            throw new McpException(problem);
        }

        var (ok, message) = await connection.CreateNetworkAsync(environments.BuildSettings(env), name.Trim(), networkDriver, @internal, ct, ipam);
        return ok ? $"Network {name} created on {env.Name}." : throw new McpException(message);
    }

    [McpServerTool(Name = "remove_network", ReadOnly = false, Destructive = true), Description("Removes a network. Docker refuses a predefined one and one a container is connected to.")]
    public static async Task<string> RemoveNetwork(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description("The name of the network.")] string name, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, message) = await connection.RemoveNetworkAsync(environments.BuildSettings(env), name.Trim(), ct);
        return ok ? $"Network {name} removed from {env.Name}." : throw new McpException(message);
    }

    [McpServerTool(Name = "connect_network", ReadOnly = false), Description("Connects a container to a network (optionally with a fixed IP and aliases), or disconnects it (connect=false).")]
    public static async Task<string> ConnectNetwork(EnvironmentService environments, IEnvironmentConnectionService connection,
        [Description("The network.")] string network, [Description("The container: name or id.")] string container,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("true = connect (default), false = disconnect.")] bool connect = true,
        [Description("A fixed IPv4 address in the network.")] string? ip = null,
        [Description("Further names of the container in the network.")] string[]? aliases = null,
        CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, message) = await connection.ConnectContainerToNetworkAsync(environments.BuildSettings(env), network.Trim(), container.Trim(), connect, ct,
            connect ? Empty(ip) : null, connect && aliases is { Length: > 0 } ? aliases : null);
        return ok ? $"{container} {(connect ? "connected to" : "disconnected from")} {network}." : throw new McpException(message);
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
