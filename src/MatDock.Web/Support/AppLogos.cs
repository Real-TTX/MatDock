using System.Text.RegularExpressions;

namespace MatDock.Web.Support;

/// <summary>
/// The logos of known apps from the selfh.st icon collection (jsDelivr), fetched by the server when someone picks one and kept with the stack as a data URL:
/// the browser never loads them from elsewhere (the CSP stays closed) and they work offline afterwards. SVG first, PNG when there is no SVG.
/// </summary>
public sealed partial class AppLogos
{
    public const string Base = "https://cdn.jsdelivr.net/gh/selfhst/icons";
    private const int MaxBytes = 256 * 1024;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<AppLogos> _logger;

    public AppLogos(IHttpClientFactory http, ILogger<AppLogos> logger)
    {
        _http = http;
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,59}\z")]
    private static partial Regex NameRegex();

    /// <summary>The name of an app as the collection writes it ("Uptime Kuma" → <c>uptime-kuma</c>), or null when nothing is left.</summary>
    public static string? Slug(string? name)
    {
        var slug = Regex.Replace((name ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return NameRegex().IsMatch(slug) ? slug : null;
    }

    /// <summary>The logo of an app as a data URL, or null when the collection has none (or it is too large, or the network fails).</summary>
    public async Task<string?> FetchAsync(string name, CancellationToken ct = default)
    {
        if (Slug(name) is not { } slug)
        {
            return null;
        }

        var client = _http.CreateClient(nameof(AppLogos));
        client.Timeout = TimeSpan.FromSeconds(10);
        foreach (var (folder, type) in new[] { ("svg", "image/svg+xml"), ("png", "image/png") })
        {
            try
            {
                using var response = await client.GetAsync($"{Base}/{folder}/{slug}.{folder}", HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
                {
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length is 0 or > MaxBytes)
                {
                    continue;
                }

                return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "The logo of {App} could not be fetched.", slug);
                return null;
            }
        }

        return null;
    }
}
