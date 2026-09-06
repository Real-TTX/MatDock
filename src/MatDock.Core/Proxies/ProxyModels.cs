namespace MatDock.Core.Proxies;

/// <summary>A route on a reverse proxy: a hostname forwarded to an upstream target.</summary>
public sealed record ProxyRoute(string Id, string Host, string Upstream, bool Tls, string? Path);

/// <summary>Create carrier for a new route.</summary>
public sealed record ProxyRouteInput(string Host, string Upstream, bool Tls, string? Path);

/// <summary>Outcome of a proxy operation (test / add / remove).</summary>
public sealed record ProxyResult(bool Ok, string Message)
{
    public static ProxyResult Fail(string message) => new(false, message);
    public static ProxyResult Success(string message) => new(true, message);
}

/// <summary>Resolved connection details (decrypted token) handed to a provider — never persisted/logged.</summary>
public sealed record ProxyTarget(string Url, string? ServerName, string? Token);
