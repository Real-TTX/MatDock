using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatDock.Core.Entities;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Proxies;

/// <summary>
/// Manages routes on a Matcad reverse proxy via its REST API.
///
/// ASSUMED API CONTRACT (adjust here once the real Matcad API is confirmed):
///   Auth:   Authorization: Bearer &lt;token&gt;
///   GET    {url}/api/routes            -> [ { "id", "host", "upstream", "tls" }, ... ]
///   POST   {url}/api/routes   body:    { "host", "upstream", "tls" }   -> 200/201 (route)
///   DELETE {url}/api/routes/{id}       -> 200/204
/// Test uses GET /api/routes.
/// </summary>
public sealed class MatcadProxyProvider : IProxyProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MatcadProxyProvider> _logger;

    public MatcadProxyProvider(IHttpClientFactory httpFactory, ILogger<MatcadProxyProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public ProxyProviderType Type => ProxyProviderType.Matcad;

    public async Task<ProxyResult> TestAsync(ProxyTarget target, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(target);
            var resp = await client.GetAsync(Url(target, "/api/routes"), ct);
            return resp.IsSuccessStatusCode
                ? ProxyResult.Success("Reachable.")
                : ProxyResult.Fail($"Matcad API returned {(int)resp.StatusCode}.");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    public async Task<IReadOnlyList<ProxyRoute>> ListRoutesAsync(ProxyTarget target, CancellationToken ct = default)
    {
        using var client = Client(target);
        var dtos = await client.GetFromJsonAsync<List<RouteDto>>(Url(target, "/api/routes"), Json, ct)
                   ?? new List<RouteDto>();

        return dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Host))
            .Select(d => new ProxyRoute(d.Id ?? d.Host!, d.Host!, d.Upstream ?? "?", d.Tls, d.Path))
            .ToList();
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
            var resp = await client.PostAsJsonAsync(Url(target, "/api/routes"),
                new RouteDto { Host = host, Upstream = upstream, Tls = route.Tls, Path = route.Path }, Json, ct);
            return resp.IsSuccessStatusCode
                ? ProxyResult.Success($"Route for {host} added.")
                : ProxyResult.Fail($"Matcad rejected the route ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    public async Task<ProxyResult> RemoveRouteAsync(ProxyTarget target, string routeId, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(target);
            var resp = await client.DeleteAsync(Url(target, $"/api/routes/{Uri.EscapeDataString(routeId)}"), ct);
            return resp.IsSuccessStatusCode
                ? ProxyResult.Success("Route removed.")
                : ProxyResult.Fail($"Matcad could not remove the route ({(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
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

    private static string Describe(Exception ex)
        => ex is HttpRequestException or TaskCanceledException
            ? "Could not reach the Matcad API (URL/network/token?)."
            : $"Error: {ex.Message}";

    private sealed class RouteDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("upstream")] public string? Upstream { get; set; }
        [JsonPropertyName("tls")] public bool Tls { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
    }
}
