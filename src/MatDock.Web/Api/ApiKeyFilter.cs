using System.Security.Cryptography;
using System.Text;

namespace MatDock.Web.Api;

/// <summary>
/// Guards every <c>/api/v1</c> endpoint with a key (<see cref="ApiKeys"/>): the engine key of the configuration (<c>MatDock__Api__Key</c>) or a named
/// key, in the <c>X-Api-Key</c> header or as a bearer token. No key at all = 503 (the API can never be reached unauthenticated), a wrong or missing
/// one = 401, a read key on a change = 403. The session cookie plays no role. Unhandled errors become a JSON 500 instead of the HTML error page.
/// </summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (await ApiKeys.CheckAsync(http, ApiKeys.IsWrite(http.Request)) is { } denied)
        {
            return denied;
        }

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

    /// <summary>The response for a request with <paramref name="presentedKey"/> against the engine key alone, or null when it may pass.</summary>
    public static IResult? Check(string? configuredKey, string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Disabled();
        }

        if (string.IsNullOrWhiteSpace(presentedKey) || !KeysMatch(configuredKey.Trim(), presentedKey.Trim()))
        {
            return Unauthorized();
        }

        return null;
    }

    /// <summary>No key exists at all (the words are part of the engine contract).</summary>
    public static IResult Disabled()
        => ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "The MatDock API is disabled: no API key is configured (MatDock__Api__Key).");

    /// <summary>A wrong or missing key (the words are part of the engine contract).</summary>
    public static IResult Unauthorized() => ApiResults.Error(StatusCodes.Status401Unauthorized, "Invalid or missing X-Api-Key.");

    /// <summary>Constant-time comparison; both sides are hashed first so neither content nor length leaks.</summary>
    public static bool KeysMatch(string expected, string presented)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
}
