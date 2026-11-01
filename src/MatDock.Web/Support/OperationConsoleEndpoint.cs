using MatDock.Core.Operations;

namespace MatDock.Web.Support;

/// <summary>
/// <c>GET /ui/operations/{id}?offset=N</c>: what the console of a page (<c>_OperationConsole</c>, <c>console.js</c>) reads of a running operation,
/// the output after <c>offset</c> and whether it ended. Signed in only (the id is a random GUID that only the page that started it knows); the API
/// has its own, keyed <c>/api/v1/operations/{id}</c>.
/// </summary>
public static class OperationConsoleEndpoint
{
    public const string Path = "/ui/operations";

    public static IResult Handle(string id, int? offset, OperationRegistry operations)
    {
        if (operations.Get(id) is not { } operation)
        {
            return Results.NotFound(new { error = "The operation is over and forgotten (an hour after it ended) or MatDock restarted." });
        }

        var (text, next) = operation.Read(offset ?? 0);
        return Results.Json(new
        {
            text,
            next,
            finished = operation.IsFinished,
            ok = operation.IsFinished ? operation.Error is null : (bool?)null,
            error = operation.Error,
        });
    }
}
