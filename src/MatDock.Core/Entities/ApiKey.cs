namespace MatDock.Core.Entities;

/// <summary>What a named API key may do.</summary>
public enum ApiKeyScope
{
    /// <summary>Only read (GET): lists, details, logs, events, metrics.</summary>
    Read = 0,

    /// <summary>Everything the API offers, as the engine key.</summary>
    Full = 1,

    /// <summary>
    /// Only the bridge (<c>/api/v1/bridge</c>): another MatDock runs its Docker commands on the environments here. That is as much as a shell with the
    /// Docker socket, so no other key opens it (not even the engine key), and this one opens nothing else.
    /// </summary>
    Bridge = 2,
}

/// <summary>
/// A named key of the API (<c>/api/v1</c>, <c>/metrics</c>) next to the engine key of the configuration: for a monitoring that only reads, a script, a CI.
/// Only its SHA-256 is stored; the key is shown once when it is made. Mapped to the <c>ApiKey</c> table.
/// </summary>
public class ApiKey : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The start of the key (<c>mdk_</c> and 8 characters), to tell keys apart in the list.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the key, hex.</summary>
    public string KeyHash { get; set; } = string.Empty;

    public ApiKeyScope Scope { get; set; }

    /// <summary>After this the key no longer works (null = never).</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
