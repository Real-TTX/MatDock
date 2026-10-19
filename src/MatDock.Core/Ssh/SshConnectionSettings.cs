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

    /// <summary>A remote Docker API (tcp://Host:Port): the Docker CLI runs on the MatDock host with DOCKER_HOST.</summary>
    public bool IsTcp => ConnectionType == ConnectionType.Tcp;

    /// <summary>The commands run on the MatDock host (Local and Tcp), not over SSH.</summary>
    public bool RunsLocally => IsLocal || IsTcp;

    /// <summary>Docker API over TCP with TLS: CA, client certificate and key (PEM); null = plain TCP.</summary>
    public string? TlsCaPem { get; init; }
    public string? TlsCertPem { get; init; }
    public string? TlsKeyPem { get; init; }

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

    /// <summary>The host key the server must show (SHA256 fingerprint); null = any key, the one seen is reported.</summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>What the SSH connection saw of the host key (filled while connecting).</summary>
    public HostKeyCheck HostKey { get; } = new();
}
