namespace MatDock.Core.Containers;

/// <summary>A container as reported by <c>docker ps -a --format '{{json .}}'</c>.</summary>
public sealed class DockerContainer
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;   // running, exited, paused, created, ...
    public string Status { get; init; } = string.Empty;  // human text, e.g. "Up 3 hours"
    public string? Ports { get; init; }

    /// <summary>docker-compose project (label com.docker.compose.project), or null for standalone.</summary>
    public string? Project { get; init; }

    /// <summary>docker-compose service name (label com.docker.compose.service).</summary>
    public string? Service { get; init; }

    // Live usage, merged from `docker stats` (only present for running containers).
    public double? CpuPercent { get; set; }
    public double? MemPercent { get; set; }
    public string? MemUsage { get; set; }  // e.g. "100MiB / 7.8GiB"

    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);
}

public enum ContainerAction
{
    Start,
    Stop,
    Restart
}

/// <summary>A mount of a container (from <c>docker inspect .Mounts</c>): a named volume or a bind.</summary>
public sealed record ContainerMount(string Type, string? Name, string? Source, string Destination, bool ReadWrite)
{
    public bool IsVolume => string.Equals(Type, "volume", StringComparison.OrdinalIgnoreCase);
}
