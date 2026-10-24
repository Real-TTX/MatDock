using MatDock.Core.Configuration;
using Microsoft.Extensions.Options;

namespace MatDock.Web.Api;

/// <summary>
/// The key of a machine caller: the header <c>X-Api-Key</c>, or a bearer token (<c>Authorization: Bearer …</c>, what a Prometheus scrape job sends).
/// The engine key (<c>MatDock__Api__Key</c>) may do everything.
/// </summary>
public static class ApiKeys
{
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
    /// The answer for a request without a fitting key (<paramref name="write"/>: one that changes something), or null when it may pass. A key that passes
    /// stands for the system: a session cookie that came along plays no role (its user's limits do not apply, its name is not in the audit).
    /// </summary>
    public static Task<IResult?> CheckAsync(HttpContext http, bool write)
    {
        var configured = http.RequestServices.GetRequiredService<IOptions<MatDockOptions>>().Value.Api.Key;
        var denied = ApiKeyFilter.Check(configured, Presented(http.Request));
        if (denied is null)
        {
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        }

        return Task.FromResult(denied);
    }
}
