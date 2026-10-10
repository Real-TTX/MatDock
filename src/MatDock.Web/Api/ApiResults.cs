using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatDock.Web.Api;

/// <summary>JSON conventions of the API (camelCase, enums as camelCase strings, nulls written) and its responses:
/// errors are always <c>{"error": "readable message"}</c>.</summary>
public static class ApiResults
{
    public static readonly JsonSerializerOptions Json = CreateOptions();

    public static IResult Ok(object value) => Results.Json(value, Json);

    public static IResult Accepted(object value) => Results.Json(value, Json, statusCode: StatusCodes.Status202Accepted);

    public static IResult Error(int statusCode, string message) => Results.Json(new { error = message }, Json, statusCode: statusCode);

    public static IResult BadRequest(string message) => Error(StatusCodes.Status400BadRequest, message);

    public static IResult NotFound(string message) => Error(StatusCodes.Status404NotFound, message);

    public static IResult Conflict(string message) => Error(StatusCodes.Status409Conflict, message);

    /// <summary>Docker (the host) did not answer as expected.</summary>
    public static IResult BadGateway(string message) => Error(StatusCodes.Status502BadGateway, message);

    /// <summary>
    /// Reads an optional JSON body regardless of the Content-Type. An empty body yields <c>default</c>; malformed
    /// JSON yields a 400 error result.
    /// </summary>
    public static async Task<(T? Value, IResult? Error)> ReadBodyAsync<T>(HttpRequest request) where T : class
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        try
        {
            return (JsonSerializer.Deserialize<T>(text, Json), null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest($"Invalid JSON body: {ex.Message}"));
        }
    }

    /// <summary>EF/SQLite hands back UTC timestamps without a kind; mark them so they serialize with a <c>Z</c>.</summary>
    public static DateTime? Utc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
