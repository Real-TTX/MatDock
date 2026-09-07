namespace MatDock.Core.Entities;

/// <summary>
/// A container image registry MatDock can authenticate against. Credentials (username + token/password)
/// are stored encrypted at rest via <c>ISecretProtector</c>. Enabled registries are logged in on the
/// target host (<c>docker login</c>) before a stack deploy/sync so private images can be pulled, and can
/// be browsed via the registry v2 API. Mapped to the <c>ContainerRegistry</c> table.
/// </summary>
public class ContainerRegistry : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Registry host, e.g. <c>ghcr.io</c>, <c>registry.example.com:5000</c> or <c>docker.io</c>.</summary>
    public string Host { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>Data-Protection-encrypted token/password.</summary>
    public string? EncryptedToken { get; set; }

    /// <summary>Browse the v2 API over plain HTTP (for local/insecure registries). HTTPS by default.</summary>
    public bool Insecure { get; set; }

    /// <summary>When false the registry is skipped for login and hidden from the browser.</summary>
    public bool Enabled { get; set; } = true;
}
