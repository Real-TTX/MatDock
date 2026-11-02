using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using YamlDotNet.RepresentationModel;

namespace MatDock.Web.Api;

/// <summary>Body of <c>PUT /api/v1/stacks/{name}</c>.</summary>
public sealed class UpsertStackBody
{
    public string? ComposeYaml { get; set; }
    public string? EnvContent { get; set; }

    /// <summary>Owner of the stack (e.g. <c>mattiverse</c>); null keeps the stored value.</summary>
    public string? ManagedBy { get; set; }

    /// <summary>Also run <c>docker compose up -d</c> (as an operation).</summary>
    public bool Deploy { get; set; }

    /// <summary>With <see cref="Deploy"/>: pull the images first.</summary>
    public bool Pull { get; set; }

    /// <summary>Secret variables to set (a name with a value) or remove (a name with null); the others stay. The answer names them, never their values.</summary>
    public Dictionary<string, string?>? Secrets { get; set; }
}

/// <summary>Body of <c>PUT /api/v1/stacks/{name}/images/{service}</c>.</summary>
public sealed class ImageOverrideBody
{
    /// <summary>The image MatDock runs for the service instead of the one in the compose file; empty or null = the compose file's again.</summary>
    public string? Image { get; set; }
}

/// <summary>Body of <c>POST /api/v1/stacks/{name}/actions/update</c>.</summary>
public sealed class UpdateActionBody
{
    /// <summary>Take a bundle backup first; the update is aborted when it fails.</summary>
    public bool BackupFirst { get; set; }
}

/// <summary><c>/api/v1/stacks</c>: list, read, upsert, lifecycle actions, delete and logs of the stacks of one environment.</summary>
public static class StacksApi
{
    public const int MaxManagedByLength = 100;
    public static readonly string[] Actions = StackOperations.Actions;

    private const string InvalidName = "Invalid stack name: lowercase a-z, 0-9, '_' and '-', starting with a letter or digit, at most 63 characters.";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/stacks", ListAsync);
        api.MapGet("/stacks/{name}", GetAsync);
        api.MapPut("/stacks/{name}", UpsertAsync);
        api.MapPost("/stacks/{name}/actions/{action}", ActionAsync);
        api.MapPut("/stacks/{name}/images/{service}", SetImageAsync);
        api.MapDelete("/stacks/{name}", DeleteAsync);
        api.MapGet("/stacks/{name}/logs", LogsAsync);
    }

    /// <summary>Operations on one stack never overlap (a delete waits for a running deploy).</summary>
    public static string LockKey(long environmentId, string name) => StackOperations.LockKey(environmentId, name);

    private static async Task<IResult> ListAsync(HttpContext http, string? managedBy,
        EnvironmentService environments, StackService stacks, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        IReadOnlyList<DockerContainer> live;
        try
        {
            live = await containers.ListAsync(env, http.RequestAborted, includeStats: false);
        }
        catch (Exception ex)
        {
            return ApiResults.BadGateway($"Docker on '{env.Name}' did not answer: {ex.Message}");
        }

        var rows = StackViews.List(env.Id, await stacks.GetAllAsync(http.RequestAborted), live);
        if (!string.IsNullOrWhiteSpace(managedBy))
        {
            rows = rows.Where(r => string.Equals(r.ManagedBy, managedBy.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return ApiResults.Ok(rows);
    }

    private static async Task<IResult> GetAsync(HttpContext http, string name,
        EnvironmentService environments, StackService stacks, ContainerService containers)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest(InvalidName);
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var stack = await stacks.GetByNameAsync(env.Id, name, http.RequestAborted);
        var live = await ProjectContainersAsync(containers, env, name, http.RequestAborted);
        if (stack is not null)
        {
            // The .env holds the secrets of the stack: only a key that may change it sees it, never a read key.
            var detail = StackViews.ToDetail(stack, live ?? [], live is null ? StackViews.UnknownState : null);
            return ApiResults.Ok(ApiCaller.Of(http).CanWrite ? detail : detail with { EnvContent = null });
        }

        return live is { Count: > 0 }
            ? ApiResults.Ok(StackViews.ToDiscoveredDetail(name, env.Id, live))
            : ApiResults.NotFound($"Stack '{name}' not found.");
    }

    private static async Task<IResult> UpsertAsync(HttpContext http, string name,
        EnvironmentService environments, StackService stacks, ContainerService containers, OperationRegistry operations)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest(InvalidName);
        }

        var (body, bodyError) = await ApiResults.ReadBodyAsync<UpsertStackBody>(http.Request);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var invalid = Validate(body);
        if (invalid is not null)
        {
            return ApiResults.BadRequest(invalid);
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (ok, message, stack, created) = await stacks.UpsertInlineAsync(
            env.Id, name, body!.ComposeYaml!, body.EnvContent, body.ManagedBy, http.RequestAborted);
        if (!ok || stack is null)
        {
            return stack is { IsGitBacked: true } ? ApiResults.Conflict(message) : ApiResults.BadRequest(message);
        }

        if (body.Secrets is { Count: > 0 } changes)
        {
            var set = changes.Where(c => c.Value is not null).ToDictionary(c => c.Key, c => c.Value!, StringComparer.Ordinal);
            var (secretsOk, secretsMessage) = await stacks.SetSecretsAsync(stack.Id, set, changes.Where(c => c.Value is null).Select(c => c.Key), http.RequestAborted);
            if (!secretsOk)
            {
                return ApiResults.BadRequest(secretsMessage);
            }

            stack = await stacks.GetAsync(stack.Id, http.RequestAborted) ?? stack;
        }

        var live = await ProjectContainersAsync(containers, env, name, http.RequestAborted);
        var view = StackViews.ToDetail(stack, live ?? [], live is null ? StackViews.UnknownState : null);
        if (!body.Deploy)
        {
            return ApiResults.Ok(new { stack = view, created });
        }

        var operation = StackOperations.Deploy(operations, env.Id, stack.Id, name, new DeployRequest(Trigger: DeployTrigger.Api, Actor: ApiCaller.Of(http).Actor), body.Pull);
        return ApiResults.Accepted(new { stack = view, created, operationId = operation.Id });
    }

    /// <summary>Validation of the upsert body; null when valid.</summary>
    public static string? Validate(UpsertStackBody? body)
    {
        if (body is null)
        {
            return "A JSON body with composeYaml is required.";
        }

        if (string.IsNullOrWhiteSpace(body.ComposeYaml))
        {
            return "composeYaml is required.";
        }

        if (body.ManagedBy is { Length: > MaxManagedByLength })
        {
            return $"managedBy is longer than {MaxManagedByLength} characters.";
        }

        if (body.Pull && !body.Deploy)
        {
            return "pull requires deploy: true.";
        }

        return ValidateComposeYaml(body.ComposeYaml);
    }

    /// <summary>The compose text must be YAML with a mapping at the top (services, volumes, …); null when it is.</summary>
    public static string? ValidateComposeYaml(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode
                ? null
                : "composeYaml must be a YAML mapping (services: …).";
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return $"composeYaml is not valid YAML: {ex.Message}";
        }
    }

    /// <summary>Makes MatDock run another image for a service than the compose file names (the file stays as it is); takes effect with the next deploy or update.</summary>
    private static async Task<IResult> SetImageAsync(HttpContext http, string name, string service,
        EnvironmentService environments, StackService stacks)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest(InvalidName);
        }

        var (body, bodyError) = await ApiResults.ReadBodyAsync<ImageOverrideBody>(http.Request);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var stack = await stacks.GetByNameAsync(env.Id, name, http.RequestAborted);
        if (stack is null)
        {
            return ApiResults.NotFound($"Stack '{name}' is not managed by MatDock.");
        }

        var (ok, message) = await stacks.SetImageOverrideAsync(stack.Id, service, body?.Image, http.RequestAborted);
        return ok ? ApiResults.Ok(new { service, image = body?.Image?.Trim() is { Length: > 0 } image ? image : null, message }) : ApiResults.BadRequest(message);
    }

    private static async Task<IResult> ActionAsync(HttpContext http, string name, string action,
        EnvironmentService environments, StackService stacks, ContainerService containers, OperationRegistry operations)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest(InvalidName);
        }

        action = action.ToLowerInvariant();
        if (!Actions.Contains(action))
        {
            return ApiResults.BadRequest($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}.");
        }

        var (body, bodyError) = await ApiResults.ReadBodyAsync<UpdateActionBody>(http.Request);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var envId = env.Id;
        var stack = await stacks.GetByNameAsync(envId, name, http.RequestAborted);
        if (stack is null)
        {
            // A discovered (not managed) compose project can still be started/stopped by its label.
            var live = await ProjectContainersAsync(containers, env, name, http.RequestAborted);
            if (live is not { Count: > 0 })
            {
                return ApiResults.NotFound($"Stack '{name}' not found.");
            }

            if (action is not ("start" or "stop" or "restart"))
            {
                return ApiResults.Conflict($"Stack '{name}' is not managed by MatDock; only start, stop and restart are possible.");
            }

            var projectOp = StackOperations.ProjectAction(operations, envId, name, action);
            return ApiResults.Accepted(new { operationId = projectOp.Id });
        }

        var stackId = stack.Id;
        var backupFirst = body?.BackupFirst ?? false;
        var operation = action == "update"
            ? StackOperations.Update(operations, envId, stackId, name, backupFirst, trigger: "api")
            : StackOperations.Action(operations, envId, stackId, name, action, new DeployRequest(Trigger: DeployTrigger.Api, Actor: ApiCaller.Of(http).Actor));

        return ApiResults.Accepted(new { operationId = operation.Id });
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string name, bool? volumes,
        EnvironmentService environments, StackService stacks, OperationRegistry operations)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest(InvalidName);
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var stack = await stacks.GetByNameAsync(env.Id, name, http.RequestAborted);
        if (stack is null)
        {
            return ApiResults.NotFound($"Stack '{name}' is not managed by MatDock.");
        }

        var operation = StackOperations.Delete(operations, env.Id, stack.Id, name, volumes ?? false);
        return ApiResults.Accepted(new { operationId = operation.Id });
    }

    private static async Task<IResult> LogsAsync(HttpContext http, string name, string? service, int? tail,
        EnvironmentService environments, ContainerService containers)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest(InvalidName);
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var live = await ProjectContainersAsync(containers, env, name, http.RequestAborted);
        if (live is null)
        {
            return ApiResults.BadGateway($"Docker on '{env.Name}' did not answer.");
        }

        var selected = live
            .Where(c => string.IsNullOrWhiteSpace(service) || string.Equals(c.Service, service.Trim(), StringComparison.Ordinal))
            .OrderBy(c => c.Service ?? c.Name, StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0)
        {
            return ApiResults.NotFound(string.IsNullOrWhiteSpace(service)
                ? $"Stack '{name}' has no containers."
                : $"Stack '{name}' has no container for service '{service}'.");
        }

        var lines = Math.Clamp(tail ?? 300, 1, 10000);
        var parts = new List<string>();
        foreach (var c in selected)
        {
            string text;
            try
            {
                text = await containers.LogsAsync(env, c.Id, lines, http.RequestAborted);
            }
            catch (Exception ex)
            {
                text = $"(logs unavailable: {ex.Message})";
            }

            // One container: the raw log. Several: prefixed like `docker compose logs`.
            parts.Add(selected.Count == 1 ? text : Prefix(c.Service ?? c.Name, text));
        }

        return ApiResults.Ok(new { text = string.Join("\n", parts) });
    }

    private static string Prefix(string label, string text)
        => string.Join("\n", text.TrimEnd('\n').Split('\n').Select(line => $"{label} | {line}"));

    /// <summary>The containers of one compose project, or null when Docker did not answer.</summary>
    private static async Task<IReadOnlyList<DockerContainer>?> ProjectContainersAsync(
        ContainerService containers, DockerEnvironment env, string project, CancellationToken ct)
    {
        try
        {
            var all = await containers.ListAsync(env, ct, includeStats: false);
            return all.Where(c => string.Equals(c.Project, project, StringComparison.Ordinal)).ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The last non-empty line of a command output (compose prints its error last).</summary>
    public static string LastLine(string? output) => StackOperations.LastLine(output);
}
