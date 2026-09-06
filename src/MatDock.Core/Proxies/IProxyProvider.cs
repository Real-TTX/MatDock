using MatDock.Core.Entities;

namespace MatDock.Core.Proxies;

/// <summary>A reverse-proxy backend MatDock can talk to (Caddy, Matcad). One implementation per provider.</summary>
public interface IProxyProvider
{
    ProxyProviderType Type { get; }

    /// <summary>Checks connectivity/auth against the proxy's API.</summary>
    Task<ProxyResult> TestAsync(ProxyTarget target, CancellationToken ct = default);

    /// <summary>Lists the routes MatDock manages on this proxy.</summary>
    Task<IReadOnlyList<ProxyRoute>> ListRoutesAsync(ProxyTarget target, CancellationToken ct = default);

    /// <summary>Creates a route (hostname → upstream).</summary>
    Task<ProxyResult> AddRouteAsync(ProxyTarget target, ProxyRouteInput route, CancellationToken ct = default);

    /// <summary>Removes a route by its provider id.</summary>
    Task<ProxyResult> RemoveRouteAsync(ProxyTarget target, string routeId, CancellationToken ct = default);
}
