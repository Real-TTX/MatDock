using MatDock.Core.Entities;

namespace MatDock.Core.Ssh;

/// <summary>
/// Everything needed to open an SSH connection to a remote Docker host, with secrets already
/// decrypted. Instances are short-lived and never persisted.
/// </summary>
public sealed class SshConnectionSettings
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    public AuthType AuthType { get; init; } = AuthType.Password;

    public string? Password { get; init; }

    public string? PrivateKeyPem { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    public int TimeoutSeconds { get; init; } = 20;
}
