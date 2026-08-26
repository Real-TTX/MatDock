namespace MatDock.Core.Entities;

/// <summary>
/// A remote Docker host reachable over SSH. Secrets (password / private key / passphrase)
/// are stored encrypted at rest via <c>ISecretProtector</c> and are never persisted in clear text.
/// Mapped to the <c>Environment</c> table.
/// </summary>
public class DockerEnvironment : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    public AuthType AuthType { get; set; } = AuthType.Password;

    /// <summary>
    /// When false the environment is deactivated: skipped by scheduled/automated runs and hidden from
    /// operational selection lists, but retained (config + secrets) and re-enableable in the admin list.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Data-Protection-encrypted SSH password (used when <see cref="AuthType"/> is Password).</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>Data-Protection-encrypted PEM private key (used when <see cref="AuthType"/> is PrivateKey).</summary>
    public string? EncryptedPrivateKey { get; set; }

    /// <summary>Data-Protection-encrypted passphrase for the private key (optional).</summary>
    public string? EncryptedPrivateKeyPassphrase { get; set; }

    public EnvironmentStatus Status { get; set; } = EnvironmentStatus.Unknown;

    /// <summary>How to reach Docker on this host (auto-detected by the connection test).</summary>
    public bool UseSudo { get; set; }

    /// <summary>Optional DOCKER_HOST (e.g. a rootless socket <c>unix:///run/user/1000/docker.sock</c>); auto-detected.</summary>
    public string? DockerHost { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    public string? LastCheckMessage { get; set; }

    public string? DockerVersion { get; set; }
}
