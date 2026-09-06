using MatDock.Core.Entities;

namespace MatDock.Core.Proxies;

/// <summary>Create/update carrier for a <see cref="ProxyConnection"/>.</summary>
public sealed class ProxyInput
{
    public string Name { get; set; } = string.Empty;

    public ProxyProviderType Provider { get; set; } = ProxyProviderType.Caddy;

    public string Url { get; set; } = string.Empty;

    public string? ServerName { get; set; }

    /// <summary>New token; blank on edit keeps the stored one.</summary>
    public string? Token { get; set; }

    public bool Enabled { get; set; } = true;
}
