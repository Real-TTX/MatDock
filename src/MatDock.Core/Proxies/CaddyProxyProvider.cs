using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Proxies;

/// <summary>
/// Manages routes on a Caddy server via its JSON Admin API. MatDock-managed routes are tagged with an
/// <c>@id</c> ("matdock-route-&lt;slug&gt;") so they can be listed and deleted (<c>/id/&lt;id&gt;</c>) without
/// touching hand-written config. Routes are appended to an existing HTTP server's <c>routes</c> array.
/// </summary>
public sealed class CaddyProxyProvider : IProxyProvider
{
    private const string IdPrefix = "matdock-route-";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CaddyProxyProvider> _logger;

    public CaddyProxyProvider(IHttpClientFactory httpFactory, ILogger<CaddyProxyProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public ProxyProviderType Type => ProxyProviderType.Caddy;

    public async Task<ProxyResult> TestAsync(ProxyTarget target, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(target);
            var resp = await client.GetAsync(Url(target, "/config/"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                return ProxyResult.Fail($"Caddy admin API returned {(int)resp.StatusCode}.");
            }

            var server = await ResolveServerAsync(client, target, ct);
            return ProxyResult.Success(server is null
                ? "Reachable — but no HTTP server found in the config yet."
                : $"Reachable — managing server \"{server}\".");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    public async Task<IReadOnlyList<ProxyRoute>> ListRoutesAsync(ProxyTarget target, CancellationToken ct = default)
    {
        using var client = Client(target);
        var json = await client.GetStringAsync(Url(target, "/config/"), ct);
        using var doc = JsonDocument.Parse(json);

        var routes = new List<ProxyRoute>();
        if (!TryGet(doc.RootElement, out var servers, "apps", "http", "servers") || servers.ValueKind != JsonValueKind.Object)
        {
            return routes;
        }

        foreach (var server in servers.EnumerateObject())
        {
            if (!server.Value.TryGetProperty("routes", out var routeArray) || routeArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var route in routeArray.EnumerateArray())
            {
                if (!route.TryGetProperty("@id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idEl.GetString()!;
                if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                routes.Add(new ProxyRoute(id, HostOf(route), UpstreamOf(route), Tls: false, Path: null));
            }
        }

        return routes;
    }

    public async Task<ProxyResult> AddRouteAsync(ProxyTarget target, ProxyRouteInput route, CancellationToken ct = default)
    {
        var host = route.Host.Trim();
        var upstream = route.Upstream.Trim();
        if (host.Length == 0 || upstream.Length == 0)
        {
            return ProxyResult.Fail("Host and upstream are required.");
        }

        try
        {
            using var client = Client(target);
            var server = await ResolveServerAsync(client, target, ct);
            if (server is null)
            {
                return ProxyResult.Fail("No HTTP server found in the Caddy config to add the route to.");
            }

            var body = new
            {
                @id = IdPrefix + Slug(host),
                match = new[] { new { host = new[] { host } } },
                handle = new object[] { new { handler = "reverse_proxy", upstreams = new[] { new { dial = upstream } } } },
            };

            // POST to an array path appends the element (Caddy admin API semantics).
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(Url(target, $"/config/apps/http/servers/{server}/routes"), content, ct);
            return resp.IsSuccessStatusCode
                ? ProxyResult.Success($"Route for {host} added.")
                : ProxyResult.Fail($"Caddy rejected the route ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    public async Task<ProxyResult> RemoveRouteAsync(ProxyTarget target, string routeId, CancellationToken ct = default)
    {
        if (!routeId.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return ProxyResult.Fail("Only MatDock-managed routes can be removed here.");
        }

        try
        {
            using var client = Client(target);
            var resp = await client.DeleteAsync(Url(target, $"/id/{routeId}"), ct);
            return resp.IsSuccessStatusCode
                ? ProxyResult.Success("Route removed.")
                : ProxyResult.Fail($"Caddy could not remove the route ({(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    /// <summary>The configured server name, or the first HTTP server in the config, or null if none.</summary>
    private static async Task<string?> ResolveServerAsync(HttpClient client, ProxyTarget target, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(target.ServerName))
        {
            return target.ServerName.Trim();
        }

        var json = await client.GetStringAsync(Url(target, "/config/"), ct);
        using var doc = JsonDocument.Parse(json);
        if (TryGet(doc.RootElement, out var servers, "apps", "http", "servers") && servers.ValueKind == JsonValueKind.Object)
        {
            foreach (var s in servers.EnumerateObject())
            {
                return s.Name;
            }
        }

        return null;
    }

    private static string HostOf(JsonElement route)
    {
        if (route.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in match.EnumerateArray())
            {
                if (m.TryGetProperty("host", out var hosts) && hosts.ValueKind == JsonValueKind.Array && hosts.GetArrayLength() > 0)
                {
                    return hosts[0].GetString() ?? "?";
                }
            }
        }
        return "?";
    }

    private static string UpstreamOf(JsonElement route)
    {
        if (route.TryGetProperty("handle", out var handles) && handles.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in handles.EnumerateArray())
            {
                if (h.TryGetProperty("upstreams", out var ups) && ups.ValueKind == JsonValueKind.Array && ups.GetArrayLength() > 0
                    && ups[0].TryGetProperty("dial", out var dial))
                {
                    return dial.GetString() ?? "?";
                }
            }
        }
        return "?";
    }

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var key in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value))
            {
                value = default;
                return false;
            }
        }
        return true;
    }

    private HttpClient Client(ProxyTarget target)
    {
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        if (!string.IsNullOrWhiteSpace(target.Token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        }
        return client;
    }

    private static string Url(ProxyTarget target, string path) => target.Url.TrimEnd('/') + path;

    /// <summary>A safe @id slug from a hostname (lowercase, non-alphanumeric → '-').</summary>
    public static string Slug(string host)
    {
        var sb = new StringBuilder(host.Length);
        foreach (var ch in host.Trim().ToLowerInvariant())
        {
            sb.Append((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') ? ch : '-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "route" : s;
    }

    private static string Describe(Exception ex)
        => ex is HttpRequestException or TaskCanceledException
            ? "Could not reach the Caddy admin API (URL/port/network?)."
            : $"Error: {ex.Message}";
}
