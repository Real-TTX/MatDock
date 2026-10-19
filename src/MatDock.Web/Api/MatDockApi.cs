using MatDock.Core;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;

namespace MatDock.Web.Api;

/// <summary>
/// The machine-to-machine REST API under <c>/api/v1</c> (the engine contract that Mattiverse drives, see
/// <c>Mattiverse/docs/ENGINES.md</c>). Guarded by <see cref="ApiKeyFilter"/>, anonymous at the cookie level. Every
/// endpoint calls the same services as the Razor pages; long actions run as <see cref="Operation"/>s.
/// </summary>
public static class MatDockApi
{
    /// <summary>Version of the API; raised when endpoints are added, so Mattiverse knows what it may call.</summary>
    public const int ApiVersion = 1;

    public const string BasePath = "/api/v1";

    public static void MapMatDockApi(this WebApplication app)
    {
        var api = app.MapGroup(BasePath)
            .AllowAnonymous() // the API key is the guard, not the session cookie
            .AddEndpointFilter<ApiKeyFilter>();

        api.MapGet("/status", StatusAsync);
        api.MapPut("/environments/local", EnsureLocalAsync);
        api.MapGet("/operations/{id}", GetOperation);

        StacksApi.Map(api);
        BackupsApi.Map(api);

    }

    private static async Task<IResult> StatusAsync(HttpContext http, EnvironmentService environments)
    {
        var all = await environments.GetAllAsync(http.RequestAborted);
        return ApiResults.Ok(new
        {
            ok = true,
            app = "matdock",
            version = AppVersion.Informational,
            apiVersion = ApiVersion,
            environments = all.Select(e => new
            {
                id = e.Id,
                name = e.Name,
                type = e.ConnectionType switch { ConnectionType.Local => "local", ConnectionType.Tcp => "tcp", _ => "ssh" },
                status = e.Status.ToString().ToLowerInvariant(),
                enabled = e.IsEnabled,
            }),
        });
    }

    private static async Task<IResult> EnsureLocalAsync(HttpContext http, EnvironmentService environments)
    {
        var (ok, message, env, created) = await environments.EnsureLocalAsync(http.RequestAborted);
        return ok && env is not null
            ? ApiResults.Ok(new { id = env.Id, name = env.Name, created })
            : ApiResults.Conflict(message);
    }

    private static IResult GetOperation(string id, int? offset, OperationRegistry operations)
    {
        var operation = operations.Get(id);
        return operation is null
            ? ApiResults.NotFound("Operation not found (unknown, or finished more than an hour ago).")
            : ApiResults.Ok(ToDto(operation, offset ?? 0));
    }

    /// <summary>The polling shape of the contract: the output after <paramref name="offset"/> and the next offset.</summary>
    public static object ToDto(Operation operation, int offset)
    {
        var (text, next) = operation.Read(offset);
        return new
        {
            id = operation.Id,
            kind = operation.Kind,
            stack = operation.Target,
            status = operation.Status.ToString().ToLowerInvariant(),
            output = text,
            offset = next,
            finished = operation.IsFinished,
            error = operation.Error,
            result = operation.Result,
            startedAt = ApiResults.Utc(operation.StartedUtc),
            finishedAt = ApiResults.Utc(operation.FinishedUtc),
        };
    }

    /// <summary>
    /// The environment a request works on: <c>?environment=&lt;id or name&gt;</c>, otherwise the local environment
    /// (the host's Docker socket). Unknown = 404; no local environment or a disabled one = 409.
    /// </summary>
    public static async Task<(DockerEnvironment? Environment, IResult? Error)> ResolveEnvironmentAsync(
        HttpContext http, EnvironmentService environments)
    {
        var selector = http.Request.Query["environment"].ToString().Trim();
        DockerEnvironment? env;
        if (selector.Length == 0)
        {
            env = await environments.FindLocalAsync(http.RequestAborted);
            if (env is null)
            {
                return (null, ApiResults.Conflict(
                    "No local environment exists. Call PUT /api/v1/environments/local or start MatDock with MatDock__Environments__EnsureLocal=true."));
            }
        }
        else
        {
            var all = await environments.GetAllAsync(http.RequestAborted);
            env = long.TryParse(selector, out var id) ? all.FirstOrDefault(e => e.Id == id) : null;
            env ??= all.FirstOrDefault(e => string.Equals(e.Name, selector, StringComparison.OrdinalIgnoreCase));
            if (env is null)
            {
                return (null, ApiResults.NotFound($"Environment '{selector}' not found."));
            }
        }

        return env.IsEnabled
            ? (env, null)
            : (null, ApiResults.Conflict($"Environment '{env.Name}' is disabled."));
    }
}
