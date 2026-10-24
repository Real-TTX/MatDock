using System.Security.Claims;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.Extensions.Options;

namespace MatDock.Web.Api;

/// <summary>
/// The key of a machine caller: the header <c>X-Api-Key</c>, or a bearer token (<c>Authorization: Bearer …</c>, what a Prometheus scrape job sends).
/// The engine key (<c>MatDock__Api__Key</c>) may do everything; a named key (System → API keys) everything or only read.
/// </summary>
public static class ApiKeys
{
    /// <summary>The authentication type of a caller with a named key (its name is the name of the identity, so the audit says who).</summary>
    public const string Scheme = "ApiKey";

    /// <summary>The key a request presents (null = none).</summary>
    public static string? Presented(HttpRequest request)
    {
        var header = request.Headers[ApiKeyFilter.HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            return header.Trim();
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
    }

    /// <summary>
    /// The answer for a request without a fitting key (<paramref name="write"/>: one that changes something), or null when it may pass: 503 when no key
    /// exists at all, 401 for a wrong or missing one, 403 for a key that may only read. A key that passes stands for the system: a session cookie that
    /// came along plays no role (its user's limits do not apply); a named key gives the audit its name.
    /// </summary>
    public static async Task<IResult?> CheckAsync(HttpContext http, bool write)
    {
        var configured = http.RequestServices.GetRequiredService<IOptions<MatDockOptions>>().Value.Api.Key;
        var presented = Presented(http.Request);
        if (!string.IsNullOrWhiteSpace(configured) && !string.IsNullOrWhiteSpace(presented) && ApiKeyFilter.KeysMatch(configured.Trim(), presented))
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity());
            return null;
        }

        // Without the service (a host that has no database) there are no named keys.
        var keys = http.RequestServices.GetService<ApiKeyService>();
        if (keys is not null && await keys.FindAsync(presented, http.RequestAborted) is { } key)
        {
            if (write && key.Scope != ApiKeyScope.Full)
            {
                return ApiResults.Error(StatusCodes.Status403Forbidden, $"The key '{key.Name}' may only read.");
            }

            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, $"API key {key.Name}")], Scheme));
            return null;
        }

        return string.IsNullOrWhiteSpace(configured) && (keys is null || !await keys.AnyAsync(http.RequestAborted))
            ? ApiKeyFilter.Disabled()
            : ApiKeyFilter.Unauthorized();
    }

    /// <summary>A request that changes something (everything but GET and HEAD).</summary>
    public static bool IsWrite(HttpRequest request) => !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
}
