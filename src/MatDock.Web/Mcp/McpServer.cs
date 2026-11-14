using System.Reflection;
using System.Text.Json;
using MatDock.Core;
using MatDock.Core.Activity;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Web.Api;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>
/// MatDock as an MCP server at <c>/mcp</c> (streamable HTTP, stateless): the tools of <c>Mcp/*Tools.cs</c> for an AI client such as Claude Code. Every
/// request carries an API key (<c>Authorization: Bearer</c> or <c>X-Api-Key</c>, the same keys as <c>/api/v1</c>): a read key sees and calls only the
/// tools that change nothing, a full key all of them. Every call of a tool that changes something goes into the activity log with the key's name.
/// </summary>
public static class McpServerSetup
{
    public const string Path = "/mcp";

    public static IServiceCollection AddMatDockMcp(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "matdock", Version = AppVersion.Informational };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpServerSetup).Assembly, ToolJson)
            .WithRequestFilters(filters =>
            {
                // A read key does not even see the tools that change something.
                filters.AddListToolsFilter(next => async (context, ct) =>
                {
                    var result = await next(context, ct);
                    if (!Caller(context.Services).CanWrite)
                    {
                        result.Tools = result.Tools.Where(t => McpToolCatalog.IsReadOnly(t.Name)).ToList();
                    }

                    return result;
                });
                filters.AddCallToolFilter(next => async (context, ct) =>
                {
                    var name = context.Params?.Name ?? string.Empty;
                    var caller = Caller(context.Services);
                    if (McpToolCatalog.IsReadOnly(name))
                    {
                        return await next(context, ct);
                    }

                    if (!caller.CanWrite)
                    {
                        return Error($"The key '{caller.KeyName}' may only read: '{name}' changes something.");
                    }

                    var result = await next(context, ct);
                    await AuditAsync(context.Services, caller, name, context.Params?.Arguments, result);
                    return result;
                });
            });
        return services;
    }

    /// <summary>The key check of every <c>/mcp</c> request (before the endpoint), and the endpoint itself.</summary>
    public static void MapMatDockMcp(this WebApplication app)
    {
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(Path), branch => branch.Use(async (ctx, next) =>
        {
            var (_, denied) = await ApiKeys.AuthenticateAsync(ctx);
            if (denied is not null)
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"matdock\"";
                await denied.ExecuteAsync(ctx);
                return;
            }

            await next();
        }));

        // Anonymous at the cookie level: the key checked above is the guard.
        app.MapMcp(Path).AllowAnonymous();
    }

    /// <summary>
    /// How the answers of the tools are written: the SDK's options, plus quotes and the like left readable (the text goes into a JSON string of the protocol
    /// anyway, never into HTML), fields without a value left out, and enums as words.
    /// </summary>
    private static readonly JsonSerializerOptions ToolJson = CreateToolJson();

    private static JsonSerializerOptions CreateToolJson()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private const string Instructions =
        "MatDock manages Docker hosts (environments): containers, compose stacks, images, volumes, networks, backups, updates and schedules. " +
        "Start with get_status to see the environments. Every tool that works on a host takes 'environment' (its id or name); it may be left out when " +
        "there is only one. Long actions (deploy, stack actions, backups, restores) wait up to waitSeconds and answer the output; when still running " +
        "they answer an operation id to follow with get_operation. Updates: list_updates shows what the last look found for every stack, check_updates looks " +
        "again, apply_updates updates all of them as their rules say (one stack: stack_action with update). Removing something and exec_in_container cannot be undone: say what you are about to do first.";

    private static ApiCaller Caller(IServiceProvider? services)
        => services?.GetService<IHttpContextAccessor>()?.HttpContext is { } http ? ApiCaller.Of(http) : new ApiCaller(null, false);

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    /// <summary>The fields of the arguments that name the object of a call, in the order they are tried.</summary>
    private static readonly string[] TargetFields = ["name", "container", "reference", "network", "service", "backup_id", "schedule_id", "scope"];

    private static async Task AuditAsync(IServiceProvider? services, ApiCaller caller, string tool, IDictionary<string, JsonElement>? arguments, CallToolResult result)
    {
        if (services?.GetService<ActivityLogService>() is not { } log)
        {
            return;
        }

        string? Field(string key) => arguments is not null && arguments.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        var message = text is null ? null : text.Length > 400 ? text[..400] + "…" : text;
        try
        {
            await log.RecordAsync(caller.Actor, "mcp", "MCP · " + tool, TargetFields.Select(Field).FirstOrDefault(v => v is not null), Field("environment"),
                result.IsError != true, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("Mcp").LogWarning(ex, "Writing the activity log failed for the tool {Tool}.", tool);
        }
    }
}

/// <summary>Which tools change nothing (<see cref="McpServerToolAttribute.ReadOnly"/>), read once from the tool classes.</summary>
public static class McpToolCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, bool>> Tools = new(() =>
        typeof(McpToolCatalog).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a?.Name is not null)
            .ToDictionary(a => a!.Name!, a => a!.ReadOnly, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, bool> All => Tools.Value;

    /// <summary>An unknown name counts as one that changes something (never wider than it should be).</summary>
    public static bool IsReadOnly(string name) => Tools.Value.TryGetValue(name, out var readOnly) && readOnly;
}

/// <summary>What the tools share: the environment of a call, the caller, waiting for an operation.</summary>
public static class McpScope
{
    /// <summary>
    /// The environment a tool works on: by id or name; without one the only enabled environment (with several the caller has to name it, so nothing
    /// lands on the wrong host).
    /// </summary>
    public static async Task<DockerEnvironment> EnvironmentAsync(EnvironmentService environments, string? environment, CancellationToken ct)
    {
        var all = await environments.GetAllAsync(ct);
        DockerEnvironment? env;
        if (string.IsNullOrWhiteSpace(environment))
        {
            var enabled = all.Where(e => e.IsEnabled).ToList();
            env = enabled.Count switch
            {
                1 => enabled[0],
                0 => throw new McpException("There is no enabled environment."),
                _ => throw new McpException($"Name the environment ('environment'): {string.Join(", ", enabled.Select(e => $"{e.Name} (id {e.Id})"))}."),
            };
        }
        else
        {
            var key = environment.Trim();
            env = (long.TryParse(key, out var id) ? all.FirstOrDefault(e => e.Id == id) : null)
                  ?? all.FirstOrDefault(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase))
                  ?? throw new McpException($"Environment '{key}' not found. There are: {string.Join(", ", all.Select(e => e.Name))}.");
        }

        return env.IsEnabled ? env : throw new McpException($"Environment '{env.Name}' is disabled.");
    }

    /// <summary>The caller of the current request (its key may change things or only read).</summary>
    public static ApiCaller Caller(IHttpContextAccessor http) => http.HttpContext is { } ctx ? ApiCaller.Of(ctx) : new ApiCaller(null, false);

    /// <summary>Waits up to <paramref name="waitSeconds"/> (0 to 600, default 120) for an operation and answers how far it got.</summary>
    public static async Task<object> WaitAsync(OperationRegistry operations, Operation operation, int? waitSeconds, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds ?? 120, 0, 600));
        if (wait > TimeSpan.Zero && !operation.IsFinished)
        {
            await Task.WhenAny(operations.WaitAsync(operation.Id), Task.Delay(wait, ct));
        }

        return View(operation);
    }

    /// <summary>An operation as a tool answers it: status, error, result and the end of its output.</summary>
    public static object View(Operation operation)
    {
        var (text, _) = operation.Read(0);
        const int Keep = 6000;
        return new
        {
            operationId = operation.Id,
            kind = operation.Kind,
            target = operation.Target,
            status = operation.Status.ToString().ToLowerInvariant(),
            error = operation.Error,
            result = operation.Result,
            output = text.Length > Keep ? "…" + text[^Keep..] : text,
            note = operation.IsFinished ? null : "Still running: follow it with get_operation.",
        };
    }

    /// <summary>The end of a long text (logs), so an answer stays readable.</summary>
    public static string Tail(string text, int maxChars = 20000) => text.Length > maxChars ? "…" + text[^maxChars..] : text;
}
