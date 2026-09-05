using MatDock.Core.Entities;

namespace MatDock.Core.Docker;

/// <summary>Outcome of a connectivity check against a remote Docker environment.</summary>
public sealed class DockerConnectionResult
{
    public bool Success { get; init; }

    public EnvironmentStatus Status { get; init; } = EnvironmentStatus.Unknown;

    /// <summary>Human-readable message (success summary or error detail).</summary>
    public string Message { get; init; } = string.Empty;

    public string? DockerVersion { get; init; }

    public string? ApiVersion { get; init; }

    public string? OsArch { get; init; }

    /// <summary>Detected access: prefix docker with <c>sudo -n</c>.</summary>
    public bool UseSudo { get; init; }

    /// <summary>Detected access: DOCKER_HOST (e.g. a rootless socket), or null for the default.</summary>
    public string? DockerHost { get; init; }

    /// <summary>Human-readable access label, e.g. "Standard", "sudo", "rootless (/run/user/1000/docker.sock)".</summary>
    public string? AccessLabel { get; init; }

    public static DockerConnectionResult Ok(
        string message, string? dockerVersion, string? apiVersion, string? osArch,
        bool useSudo = false, string? dockerHost = null, string? accessLabel = null) => new()
    {
        Success = true,
        Status = EnvironmentStatus.Online,
        Message = message,
        DockerVersion = dockerVersion,
        ApiVersion = apiVersion,
        OsArch = osArch,
        UseSudo = useSudo,
        DockerHost = dockerHost,
        AccessLabel = accessLabel
    };

    public static DockerConnectionResult Fail(string message, EnvironmentStatus status = EnvironmentStatus.Error) => new()
    {
        Success = false,
        Status = status,
        Message = message
    };
}

/// <summary>A Docker volume as shown in the environment's volume list.</summary>
public sealed class DockerVolume
{
    public string Name { get; init; } = string.Empty;

    public string Driver { get; init; } = string.Empty;

    public string? Mountpoint { get; init; }

    public string? Scope { get; init; }

    public string? CreatedAt { get; init; }

    public long? SizeBytes { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>Outcome of a prune (volumes or networks): whether it ran, how many were removed, raw detail.</summary>
public sealed record PruneResult(bool Success, int Removed, string Detail)
{
    public static PruneResult Fail(string message) => new(false, 0, message);
}

/// <summary>A Docker network as shown in the environment's network list.</summary>
public sealed class DockerNetwork
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Driver { get; init; } = string.Empty;

    public string? Scope { get; init; }

    /// <summary>True for internal (non-routable) networks.</summary>
    public bool Internal { get; init; }

    public string? CreatedAt { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>The three predefined networks that Docker never lets you remove or prune.</summary>
    public bool IsPredefined =>
        Name is "bridge" or "host" or "none";
}

/// <summary>Full detail of a Docker network from <c>docker network inspect</c>.</summary>
public sealed class DockerNetworkDetail
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Driver { get; init; } = string.Empty;

    public string? Scope { get; init; }

    public bool Internal { get; init; }

    public bool Attachable { get; init; }

    public bool EnableIPv6 { get; init; }

    public string? CreatedAt { get; init; }

    /// <summary>IPAM subnets, e.g. "172.18.0.0/16 → 172.18.0.1".</summary>
    public IReadOnlyList<string> Subnets { get; init; } = new List<string>();

    /// <summary>Names of the containers currently attached to this network.</summary>
    public IReadOnlyList<string> Containers { get; init; } = new List<string>();

    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>Full detail of a Docker volume from <c>docker volume inspect</c> (incl. driver options).</summary>
public sealed class DockerVolumeDetail
{
    public string Name { get; init; } = string.Empty;

    public string Driver { get; init; } = string.Empty;

    public string? Mountpoint { get; init; }

    public string? Scope { get; init; }

    public string? CreatedAt { get; init; }

    /// <summary>Driver options; for CIFS/NFS volumes this holds the remote share (device), o, type.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>The remote share/device for CIFS/NFS volumes (<c>Options["device"]</c>), otherwise null.</summary>
    public string? Device => Options.TryGetValue("device", out var d) && !string.IsNullOrEmpty(d) ? d : null;

    /// <summary>True when the volume is a remote mount (has a device option), e.g. SMB/CIFS or NFS.</summary>
    public bool IsRemote => Device is not null;
}
