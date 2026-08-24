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

    public static DockerConnectionResult Ok(string message, string? dockerVersion, string? apiVersion, string? osArch) => new()
    {
        Success = true,
        Status = EnvironmentStatus.Online,
        Message = message,
        DockerVersion = dockerVersion,
        ApiVersion = apiVersion,
        OsArch = osArch
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
