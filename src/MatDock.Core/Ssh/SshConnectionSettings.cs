using MatDock.Core.Entities;

namespace MatDock.Core.Ssh;

/// <summary>
/// Everything needed to open an SSH connection to a remote Docker host, with secrets already
/// decrypted. Instances are short-lived and never persisted.
/// </summary>
public sealed class SshConnectionSettings
{
    /// <summary>Whether commands run over SSH (default) or locally against the mounted Docker socket.</summary>
    public ConnectionType ConnectionType { get; init; } = ConnectionType.Ssh;

    public bool IsLocal => ConnectionType == ConnectionType.Local;

    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    public AuthType AuthType { get; init; } = AuthType.Password;

    public string? Password { get; init; }

    public string? PrivateKeyPem { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    public int TimeoutSeconds { get; init; } = 20;

    /// <summary>Prefix docker with <c>sudo -n</c> (auto-detected).</summary>
    public bool UseSudo { get; init; }

    /// <summary>Optional DOCKER_HOST value, e.g. a rootless socket (auto-detected).</summary>
    public string? DockerHost { get; init; }
}
