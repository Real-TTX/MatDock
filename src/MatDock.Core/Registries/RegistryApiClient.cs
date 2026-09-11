using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Registries;

/// <summary>
/// Talks to a container registry's HTTP v2 API from MatDock itself (not via an environment). Implements the
/// standard token-auth handshake: an unauthenticated request that returns <c>401</c> with a
/// <c>WWW-Authenticate: Bearer realm=…,service=…,scope=…</c> challenge triggers a token fetch (with Basic
/// auth when credentials are set), after which the request is retried with the bearer token. Registries that
/// answer with <c>WWW-Authenticate: Basic</c> are retried with Basic auth directly.
/// </summary>
public sealed partial class RegistryApiClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RegistryApiClient> _logger;

    public RegistryApiClient(IHttpClientFactory httpFactory, ILogger<RegistryApiClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-z0-9]+([._-][a-z0-9]+)*(/[a-z0-9]+([._-][a-z0-9]+)*)*$")]
    private static partial Regex RepoRegex();

    public static bool IsValidRepo(string? repo) => !string.IsNullOrEmpty(repo) && RepoRegex().IsMatch(repo);

    public async Task<RegistryResult> TestAsync(RegistryLogin login, CancellationToken ct = default)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await SendAuthedAsync(client, login, "/v2/", scope: null, ct);
            if (resp.IsSuccessStatusCode)
            {
                return RegistryResult.Success("Reachable and authenticated.");
            }
            return resp.StatusCode == HttpStatusCode.Unauthorized
                ? RegistryResult.Fail("Reachable, but authentication failed (check username/token).")
                : RegistryResult.Fail($"Registry returned {(int)resp.StatusCode}.");
        }
        catch (Exception ex)
        {
            return RegistryResult.Fail(Describe(ex));
        }
    }

    public async Task<RegistryCatalog> ListRepositoriesAsync(RegistryLogin login, int limit = 200, CancellationToken ct = default)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var resp = await SendAuthedAsync(client, login, $"/v2/_catalog?n={limit}", "registry:catalog:*", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new RegistryCatalog(Array.Empty<string>(), CatalogError(login, resp.StatusCode));
            }

            var payload = await resp.Content.ReadFromJsonSafeAsync<CatalogDto>(ct);
            return new RegistryCatalog(payload?.Repositories ?? new List<string>(), null);
        }
        catch (Exception ex)
        {
            return new RegistryCatalog(Array.Empty<string>(), Describe(ex));
        }
    }

    public async Task<RegistryTagList> ListTagsAsync(RegistryLogin login, string repo, CancellationToken ct = default)
    {
        if (!IsValidRepo(repo))
        {
            return new RegistryTagList(repo, Array.Empty<string>(), "Invalid repository name.");
        }

        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var resp = await SendAuthedAsync(client, login, $"/v2/{repo}/tags/list", $"repository:{repo}:pull", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new RegistryTagList(repo, Array.Empty<string>(), $"Registry returned {(int)resp.StatusCode}.");
            }

            var payload = await resp.Content.ReadFromJsonSafeAsync<TagsDto>(ct);
            var tags = payload?.Tags ?? new List<string>();
            // Newest-ish first: registries return tags unsorted; a stable descending sort is friendlier.
            return new RegistryTagList(repo, tags.OrderByDescending(t => t, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (Exception ex)
        {
            return new RegistryTagList(repo, Array.Empty<string>(), Describe(ex));
        }
    }

    /// <summary>Manifest digest (<c>Docker-Content-Digest</c>) for repo:tag, or null if unavailable.</summary>
    public async Task<string?> GetDigestAsync(RegistryLogin login, string repo, string tag, CancellationToken ct = default)
    {
        if (!IsValidRepo(repo) || string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await SendAuthedAsync(client, login, $"/v2/{repo}/manifests/{Uri.EscapeDataString(tag)}",
                $"repository:{repo}:pull", ct, HttpMethod.Head, ManifestAccept);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            return resp.Headers.TryGetValues("Docker-Content-Digest", out var v) ? v.FirstOrDefault() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Digest lookup failed for {Repo}:{Tag} on {Host}.", repo, tag, login.Host);
            return null;
        }
    }

    private const string ManifestAccept =
        "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, " +
        "application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json";

    private async Task<HttpResponseMessage> SendAuthedAsync(HttpClient client, RegistryLogin login, string path, string? scope,
        CancellationToken ct, HttpMethod? method = null, string? accept = null)
    {
        var url = BaseUrl(login) + path;

        var first = await client.SendAsync(Request(login, url, bearer: null, method: method, accept: accept), HttpCompletionOption.ResponseHeadersRead, ct);
        if (first.StatusCode != HttpStatusCode.Unauthorized || first.Headers.WwwAuthenticate.Count == 0)
        {
            return first;
        }

        var challenge = first.Headers.WwwAuthenticate.First();
        if (string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            first.Dispose();
            return await client.SendAsync(Request(login, url, bearer: null, forceBasic: true, method: method, accept: accept), HttpCompletionOption.ResponseHeadersRead, ct);
        }

        if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return first; // unknown scheme — let the caller see the 401
        }

        var token = await FetchBearerTokenAsync(client, login, challenge.Parameter ?? string.Empty, scope, ct);
        first.Dispose();
        if (token is null)
        {
            // Couldn't get a token; retry once more anonymously so the caller sees a meaningful status.
            return await client.SendAsync(Request(login, url, bearer: null, method: method, accept: accept), HttpCompletionOption.ResponseHeadersRead, ct);
        }

        return await client.SendAsync(Request(login, url, bearer: token, method: method, accept: accept), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static HttpRequestMessage Request(RegistryLogin login, string url, string? bearer, bool forceBasic = false,
        HttpMethod? method = null, string? accept = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept ?? "application/json");
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else if (forceBasic && HasCreds(login))
        {
            req.Headers.Authorization = BasicHeader(login);
        }
        return req;
    }

    private async Task<string?> FetchBearerTokenAsync(HttpClient client, RegistryLogin login, string challengeParams, string? scope, CancellationToken ct)
    {
        var parts = ParseChallenge(challengeParams);
        if (!parts.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
        {
            return null;
        }

        var query = new StringBuilder();
        if (parts.TryGetValue("service", out var service) && !string.IsNullOrWhiteSpace(service))
        {
            query.Append("service=").Append(Uri.EscapeDataString(service));
        }
        var effectiveScope = scope ?? (parts.TryGetValue("scope", out var s) ? s : null);
        if (!string.IsNullOrWhiteSpace(effectiveScope))
        {
            if (query.Length > 0) { query.Append('&'); }
            query.Append("scope=").Append(Uri.EscapeDataString(effectiveScope));
        }

        var tokenUrl = query.Length > 0 ? $"{realm}?{query}" : realm;
        using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        if (HasCreds(login))
        {
            tokenReq.Headers.Authorization = BasicHeader(login);
        }

        using var resp = await client.SendAsync(tokenReq, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await resp.Content.ReadFromJsonSafeAsync<TokenDto>(ct);
        var token = dto?.Token ?? dto?.AccessToken;
        if (token is null)
        {
            _logger.LogDebug("Registry token endpoint returned no token for {Host}.", login.Host);
        }
        return token;
    }

    /// <summary>Registry API base, e.g. https://ghcr.io. Docker Hub's canonical host maps to registry-1.docker.io.</summary>
    private static string BaseUrl(RegistryLogin login)
    {
        var host = login.Host.Trim().TrimEnd('/');
        if (host.Equals("docker.io", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase))
        {
            host = "registry-1.docker.io";
        }
        return (login.Insecure ? "http://" : "https://") + host;
    }

    private static bool HasCreds(RegistryLogin login)
        => !string.IsNullOrWhiteSpace(login.Username) && !string.IsNullOrWhiteSpace(login.Token);

    private static AuthenticationHeaderValue BasicHeader(RegistryLogin login)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.Username}:{login.Token}")));

    private static Dictionary<string, string> ParseChallenge(string parameter)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(parameter, "(?<k>[a-zA-Z]+)=\"(?<v>[^\"]*)\""))
        {
            map[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        return map;
    }

    private static string CatalogError(RegistryLogin login, HttpStatusCode code)
    {
        var host = login.Host.Trim();
        if ((host.Equals("docker.io", StringComparison.OrdinalIgnoreCase) ||
             host.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase)) &&
            (code == HttpStatusCode.NotFound || code == HttpStatusCode.Unauthorized))
        {
            return "Docker Hub does not expose the _catalog endpoint. Browse a specific repository by name instead.";
        }
        return code == HttpStatusCode.NotFound
            ? "This registry does not expose a catalog (_catalog). Try browsing a repository by name."
            : $"Registry returned {(int)code}.";
    }

    private static string Describe(Exception ex)
        => ex is HttpRequestException or TaskCanceledException
            ? "Could not reach the registry (host/network/TLS?)."
            : $"Error: {ex.Message}";

    private sealed class CatalogDto { public List<string>? Repositories { get; set; } }
    private sealed class TagsDto { public List<string>? Tags { get; set; } }
    private sealed class TokenDto
    {
        public string? Token { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    }
}

internal static class HttpContentJsonExtensions
{
    /// <summary>Deserializes JSON, returning null on any content/parse error instead of throwing.</summary>
    public static async Task<T?> ReadFromJsonSafeAsync<T>(this HttpContent content, CancellationToken ct)
    {
        try
        {
            var stream = await content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch
        {
            return default;
        }
    }
}
