using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MatDock.Core.Configuration;
using Microsoft.Extensions.Options;

namespace MatDock.Web.Api;

/// <summary>
/// Guards every <c>/api/v1</c> endpoint with the shared secret in the <c>X-Api-Key</c> header
/// (<c>MatDock__Api__Key</c>): no key configured = 503 (the API can never be reached unauthenticated), a wrong or
/// missing key = 401. The session cookie plays no role: whatever identity it produced is dropped, so API changes
/// are audited as system changes. Unhandled errors become a JSON 500 instead of the HTML error page.
/// </summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var configured = http.RequestServices.GetRequiredService<IOptions<MatDockOptions>>().Value.Api.Key;
        var denied = Check(configured, http.Request.Headers[HeaderName].ToString());
        if (denied is not null)
        {
            return denied;
        }

        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<ApiKeyFilter>()
                .LogError(ex, "API request {Method} {Path} failed.", http.Request.Method, http.Request.Path);
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>The response for a request with <paramref name="presentedKey"/>, or null when it may pass.</summary>
    public static IResult? Check(string? configuredKey, string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable,
                "The MatDock API is disabled: no API key is configured (MatDock__Api__Key).");
        }

        if (string.IsNullOrWhiteSpace(presentedKey) || !KeysMatch(configuredKey.Trim(), presentedKey.Trim()))
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "Invalid or missing X-Api-Key.");
        }

        return null;
    }

    /// <summary>Constant-time comparison; both sides are hashed first so neither content nor length leaks.</summary>
    public static bool KeysMatch(string expected, string presented)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
}
