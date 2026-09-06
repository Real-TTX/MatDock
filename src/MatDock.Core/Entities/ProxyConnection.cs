namespace MatDock.Core.Entities;

/// <summary>
/// A connection to a reverse proxy whose routes MatDock manages (Caddy Admin API or Matcad REST API).
/// The optional API token is stored encrypted. Mapped to the <c>ProxyConnection</c> table.
/// </summary>
public class ProxyConnection : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public ProxyProviderType Provider { get; set; } = ProxyProviderType.Caddy;

    /// <summary>Admin/API base URL, e.g. <c>http://10.0.0.5:2019</c> (Caddy) or <c>https://matcad.local</c> (Matcad).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Caddy only: the HTTP server name in the config (blank = auto-detect the first server).</summary>
    public string? ServerName { get; set; }

    /// <summary>Optional API token/bearer, encrypted at rest.</summary>
    public string? EncryptedToken { get; set; }

    public bool Enabled { get; set; } = true;
}
