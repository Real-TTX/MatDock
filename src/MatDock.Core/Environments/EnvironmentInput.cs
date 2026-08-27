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

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    public AuthType AuthType { get; set; } = AuthType.Password;

    public string? Password { get; set; }

    public string? PrivateKeyPem { get; set; }

    public string? PrivateKeyPassphrase { get; set; }
}
