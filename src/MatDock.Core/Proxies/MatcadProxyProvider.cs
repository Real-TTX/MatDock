using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatDock.Core.Entities;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Proxies;

/// <summary>
/// Manages routes on a Matcad reverse proxy via its machine-to-machine REST API.
///
/// Contract (verified against Matcad's <c>Api/MatcadApi.cs</c>, the same one matOS uses):
///   Base:   {url}/api/v1            (Matcad listens on :4433)
///   Auth:   header X-Api-Key: &lt;key&gt;   (no key configured on Matcad -> 503; wrong key -> 401)
///   GET    /status                  -> { ok, baseDomain, counts, ... }         (connectivity check)
///   GET    /routes/manual           -> [ { id, name, host, upstream, enabled, ... }, ... ]  (editable routes)
///   POST   /routes  body:           { host, upstream, enabled, wildcard, ... }
///                                   -> { route, applied: { ok, error } }        (applied = Caddy reload)
///   DELETE /routes/{id}             -> { ok, error }
///
/// TLS is not a per-route toggle on Matcad — Caddy auto-manages HTTPS/certs — so the
/// shared "TLS" flag from the MatDock form is not sent. Path routing is likewise not
/// modelled by Matcad, so <see cref="ProxyRouteInput.Path"/> is ignored here.
/// </summary>
public sealed class MatcadProxyProvider : IProxyProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
            var resp = await client.GetAsync(Url(target, "/status"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                return ProxyResult.Fail(await DescribeStatusAsync(resp, ct));
            }

            var status = await resp.Content.ReadFromJsonAsync<StatusDto>(Json, ct);
            return status?.Ok == true
                ? ProxyResult.Success($"Reachable — base domain: {status.BaseDomain ?? "(none)"}.")
                : ProxyResult.Success("Reachable.");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    public async Task<IReadOnlyList<ProxyRoute>> ListRoutesAsync(ProxyTarget target, CancellationToken ct = default)
    {
        using var client = Client(target);
        var dtos = await client.GetFromJsonAsync<List<RouteDto>>(Url(target, "/routes/manual"), Json, ct)
                   ?? new List<RouteDto>();

        return dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Host))
            // Matcad terminates TLS via Caddy for every host route, so Tls is always true here.
            .Select(d => new ProxyRoute(d.Id.ToString(), d.Host!, d.Upstream ?? "—", Tls: true, Path: null))
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
            // Matcad's RouteInput binds case-insensitively; a plain host reverse-proxy route.
            var body = new { host, upstream, enabled = true, wildcard = false, name = host };
            var resp = await client.PostAsJsonAsync(Url(target, "/routes"), body, Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return ProxyResult.Fail(await DescribeStatusAsync(resp, ct));
            }

            // A 200 means the route was stored; check whether Caddy actually reloaded it.
            var result = await resp.Content.ReadFromJsonAsync<AddResponse>(Json, ct);
            if (result?.Applied is { Ok: false } applied)
            {
                return ProxyResult.Fail($"Route saved, but Caddy did not apply it: {applied.Error ?? "unknown error"}.");
            }

            return ProxyResult.Success($"Route for {host} added.");
        }
        catch (Exception ex)
        {
            return ProxyResult.Fail(Describe(ex));
        }
    }

    public async Task<ProxyResult> RemoveRouteAsync(ProxyTarget target, string routeId, CancellationToken ct = default)
    {
        if (!long.TryParse(routeId, out var id) || id <= 0)
        {
            return ProxyResult.Fail("Invalid Matcad route id.");
        }

        try
        {
            using var client = Client(target);
            var resp = await client.DeleteAsync(Url(target, $"/routes/{id}"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                return ProxyResult.Fail(await DescribeStatusAsync(resp, ct));
            }

            var result = await resp.Content.ReadFromJsonAsync<AppliedDto>(Json, ct);
            return result is { Ok: false }
                ? ProxyResult.Fail($"Route removed, but Caddy did not apply it: {result.Error ?? "unknown error"}.")
                : ProxyResult.Success("Route removed.");
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
            client.DefaultRequestHeaders.Add("X-Api-Key", target.Token);
        }
        return client;
    }

    private static string Url(ProxyTarget target, string path) => target.Url.TrimEnd('/') + "/api/v1" + path;

    private static async Task<string> DescribeStatusAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var code = (int)resp.StatusCode;
        var hint = code switch
        {
            401 => "invalid or missing X-Api-Key",
            503 => "Matcad API is disabled (no API key set on Matcad)",
            _ => null,
        };
        if (hint is not null) return $"Matcad API returned {code}: {hint}.";

        var raw = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        return raw.Length is > 0 and <= 300
            ? $"Matcad API returned {code}: {raw}"
            : $"Matcad API returned {code}.";
    }

    private static string Describe(Exception ex)
        => ex is HttpRequestException or TaskCanceledException
            ? "Could not reach the Matcad API (URL/network/API key?)."
            : $"Error: {ex.Message}";

    private sealed class StatusDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("baseDomain")] public string? BaseDomain { get; set; }
    }

    private sealed class RouteDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("upstream")] public string? Upstream { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    }

    private sealed class AddResponse
    {
        [JsonPropertyName("applied")] public AppliedDto? Applied { get; set; }
    }

    private sealed class AppliedDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
