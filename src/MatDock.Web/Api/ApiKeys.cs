using System.Security.Claims;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.Extensions.Options;

namespace MatDock.Web.Api;

/// <summary>Who called with a key: its name (null = the engine key) and whether it may change anything. Kept in <c>HttpContext.Items</c> for the request.</summary>
public sealed record ApiCaller(string? KeyName, bool CanWrite, bool IsBridge = false)
{
    public const string ItemKey = "matdock.api-caller";

    /// <summary>The caller of this request; a request no key let in may only read (a fallback that never widens anything).</summary>
    public static ApiCaller Of(HttpContext http) => http.Items[ItemKey] as ApiCaller ?? new ApiCaller(null, false);

    /// <summary>How the activity log names it.</summary>
    public string Actor => KeyName is null ? "API" : $"API key {KeyName}";
}

/// <summary>
/// The key of a machine caller: the header <c>X-Api-Key</c>, or a bearer token (<c>Authorization: Bearer …</c>, what a Prometheus scrape job or an MCP
/// client sends). The engine key (<c>MatDock__Api__Key</c>) may do everything; a named key (System → API keys) everything or only read.
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
    /// The caller of a request, or the answer for one without a fitting key: 503 when no key exists at all, 401 for a wrong or missing one. A key that
    /// passes stands for the system: a session cookie that came along plays no role (its user's limits do not apply); a named key gives the audit its name.
    /// The caller is kept for the request (<see cref="ApiCaller.Of"/>).
    /// </summary>
    public static async Task<(ApiCaller? Caller, IResult? Denied)> AuthenticateAsync(HttpContext http, bool bridge = false)
    {
        var configured = http.RequestServices.GetRequiredService<IOptions<MatDockOptions>>().Value.Api.Key;
        var presented = Presented(http.Request);
        ApiCaller? caller = null;
        if (!string.IsNullOrWhiteSpace(configured) && !string.IsNullOrWhiteSpace(presented) && ApiKeyFilter.KeysMatch(configured.Trim(), presented))
        {
            caller = new ApiCaller(null, true);
            http.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        // Without the service (a host that has no database) there are no named keys.
        var keys = http.RequestServices.GetService<ApiKeyService>();
        if (caller is null && keys is not null && await keys.FindAsync(presented, http.RequestAborted) is { } key)
        {
            caller = new ApiCaller(key.Name, key.Scope == ApiKeyScope.Full, key.Scope == ApiKeyScope.Bridge);
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, caller.Actor)], Scheme));
        }

        if (caller is not null)
        {
            // The bridge is as much as a shell on the Docker hosts: only a bridge key opens it, and a bridge key opens nothing else.
            if (caller.IsBridge != bridge)
            {
                return (null, ApiResults.Error(StatusCodes.Status403Forbidden, bridge
                    ? "The bridge needs a bridge key (System → API keys, \"Bridge\")."
                    : $"The key '{caller.KeyName}' is a bridge key: it opens only the bridge of another MatDock."));
            }

            http.Items[ApiCaller.ItemKey] = caller;
            return (caller, null);
        }

        return (null, string.IsNullOrWhiteSpace(configured) && (keys is null || !await keys.AnyAsync(http.RequestAborted))
            ? ApiKeyFilter.Disabled()
            : ApiKeyFilter.Unauthorized());
    }

    /// <summary>The answer for a request without a fitting key (<paramref name="write"/>: one that changes something; 403 for a read key), or null when it may pass.</summary>
    public static async Task<IResult?> CheckAsync(HttpContext http, bool write)
    {
        var (caller, denied) = await AuthenticateAsync(http);
        if (denied is not null)
        {
            return denied;
        }

        return write && !caller!.CanWrite
            ? ApiResults.Error(StatusCodes.Status403Forbidden, $"The key '{caller.KeyName}' may only read.")
            : null;
    }

    /// <summary>A request that changes something (everything but GET and HEAD).</summary>
    public static bool IsWrite(HttpRequest request) => !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
}
