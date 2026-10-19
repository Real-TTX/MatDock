using MatDock.Core.Entities;

namespace MatDock.Core.Environments;

/// <summary>
/// Carrier for create/update operations on an environment. Secret fields are plaintext here and are
/// encrypted by <see cref="EnvironmentService"/> before persisting. On update, a <c>null</c>/empty
/// secret means "keep the currently stored value".
/// </summary>
public sealed class EnvironmentInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? BaseUrl { get; set; }

    public ConnectionType ConnectionType { get; set; } = ConnectionType.Ssh;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    public AuthType AuthType { get; set; } = AuthType.Password;

    public string? Password { get; set; }

    public string? PrivateKeyPem { get; set; }

    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Docker API over TCP: CA, client certificate and client key (PEM); all empty = no TLS. A blank key on edit keeps the stored one.</summary>
    public string? TlsCa { get; set; }
    public string? TlsCert { get; set; }
    public string? TlsKey { get; set; }
}
