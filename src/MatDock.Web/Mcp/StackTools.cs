using System.ComponentModel;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Web.Api;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>The compose stacks of an environment: list, compose file, logs, updates, deploy, actions, services, remove.</summary>
[McpServerToolType]
public static class StackTools
{
    private const string EnvironmentHelp = "The environment: id or name (may be left out when there is only one).";
    private const string StackHelp = "The stack (the compose project name: lowercase a-z, 0-9, _ and -).";
    private const string WaitHelp = "Seconds to wait for the end (0 to 600, default 120); still running = an operation id for get_operation.";

    [McpServerTool(Name = "list_stacks", ReadOnly = true), Description("The compose stacks of an environment, managed by MatDock or only found by their containers: name, managed, owner, state (running, stopped, partial, unhealthy, missing) and containers.")]
    public static async Task<object> ListStacks(EnvironmentService environments, StackService stacks, ContainerService containers,
        [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var live = await containers.ListAsync(env, ct, includeStats: false);
        return StackViews.List(env.Id, await stacks.GetAllAsync(ct), live);
    }

    [McpServerTool(Name = "get_stack", ReadOnly = true), Description("One stack: state, containers, its compose file, the images MatDock runs instead of the file's (overrides) and, for a key that may change things, its .env.")]
    public static async Task<object> GetStack(EnvironmentService environments, StackService stacks, ContainerService containers, IHttpContextAccessor http,
        [Description(StackHelp)] string name, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(name);
        var stack = await stacks.GetByNameAsync(env.Id, name, ct);
        var live = (await containers.ListAsync(env, ct, includeStats: false)).Where(c => c.Project == name).ToList();
        if (stack is not null)
        {
            // The .env holds the secrets of the stack: never for a read key.
            var detail = StackViews.ToDetail(stack, live);
            return McpScope.Caller(http).CanWrite ? detail : detail with { EnvContent = null };
        }

        return live.Count > 0 ? StackViews.ToDiscoveredDetail(name, env.Id, live) : throw new McpException($"Stack '{name}' not found on {env.Name}.");
    }

    [McpServerTool(Name = "stack_logs", ReadOnly = true), Description("The last lines of the logs of a stack: all its containers (each line prefixed with the service) or one service.")]
    public static async Task<string> StackLogs(EnvironmentService environments, ContainerService containers,
        [Description(StackHelp)] string name, [Description(EnvironmentHelp)] string? environment = null,
        [Description("Only this service.")] string? service = null,
        [Description("Lines per container (1 to 5000, default 200).")] int? tail = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(name);
        var selected = (await containers.ListAsync(env, ct, includeStats: false))
            .Where(c => c.Project == name && (string.IsNullOrWhiteSpace(service) || c.Service == service.Trim()))
            .OrderBy(c => c.Service ?? c.Name, StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0)
        {
            throw new McpException(string.IsNullOrWhiteSpace(service) ? $"Stack '{name}' has no containers." : $"Stack '{name}' has no container for service '{service}'.");
        }

        var parts = new List<string>();
        foreach (var c in selected)
        {
            var text = await containers.LogsAsync(env, c.Id, Math.Clamp(tail ?? 200, 1, 5000), ct);
            parts.Add(selected.Count == 1 ? text : string.Join("\n", text.TrimEnd('\n').Split('\n').Select(line => $"{c.Service ?? c.Name} | {line}")));
        }

        return McpScope.Tail(string.Join("\n", parts));
    }

    [McpServerTool(Name = "check_stack_updates", ReadOnly = true), Description("Asks the registries whether the images of a stack have newer versions: per service the image, whether a newer image is there under its tag, a newer version tag, pinned services.")]
    public static async Task<object> CheckStackUpdates(EnvironmentService environments, UpdateService updates,
        [Description(StackHelp)] string name, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(name);
        var report = await updates.CheckStackAsync(env, name, ct, newerTags: true);
        return report.Error is not null
            ? throw new McpException(report.Error)
            : new { stack = name, updatesAvailable = report.UpdatesAvailable, images = report.Images.Select(i => new { i.Service, i.Image, i.UpdateAvailable, i.NewerTag, i.Pinned, i.Note }) };
    }

    [McpServerTool(Name = "deploy_stack", ReadOnly = false), Description("Creates or changes a managed stack from a compose file (and an optional .env) and deploys it (docker compose up -d; with pull the images are pulled first). deploy=false only saves it. A stack that comes from Git cannot be changed this way.")]
    public static async Task<object> DeployStack(EnvironmentService environments, StackService stacks, OperationRegistry operations, IHttpContextAccessor http,
        [Description(StackHelp)] string name,
        [Description("The compose file (YAML).")] string composeYaml,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("The .env of the stack (KEY=value lines); left out = the stored one stays.")] string? envContent = null,
        [Description("Deploy it (default true).")] bool deploy = true,
        [Description("Pull the images first.")] bool pull = false,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(name);
        if (StacksApi.ValidateComposeYaml(composeYaml) is { } invalid)
        {
            throw new McpException(invalid);
        }

        var existing = await stacks.GetByNameAsync(env.Id, name, ct);
        var (ok, message, stack, created) = await stacks.UpsertInlineAsync(env.Id, name, composeYaml, envContent ?? existing?.EnvContent, null, ct);
        if (!ok || stack is null)
        {
            throw new McpException(message);
        }

        if (!deploy)
        {
            return new { stack = name, created, deployed = false, message };
        }

        var operation = StackOperations.Deploy(operations, env.Id, stack.Id, name, new DeployRequest(Trigger: DeployTrigger.Mcp, Actor: McpScope.Caller(http).Actor), pull);
        return new { stack = name, created, deploy = await McpScope.WaitAsync(operations, operation, waitSeconds, ct) };
    }

    [McpServerTool(Name = "stack_action", ReadOnly = false), Description("start, stop, restart, pull, redeploy or update a stack. update asks the registries, pulls and re-creates only what changed (optionally after a bundle backup), with the health check and rollback of its update rule. A stack MatDock does not manage can be started, stopped, restarted and updated: update pulls the tags the registry moved and starts those services again in place with the stack's own compose files, as the rule of its environment says; when MatDock cannot (its files are not on the Docker host) it fails with the reason and touches nothing.")]
    public static async Task<object> StackAction(EnvironmentService environments, StackService stacks, ContainerService containers, OperationRegistry operations, IHttpContextAccessor http,
        [Description(StackHelp)] string name,
        [Description("start, stop, restart, pull, redeploy or update.")] string action,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("update: take a bundle backup first; the update is not made when it fails.")] bool backupFirst = false,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(name);
        action = action.Trim().ToLowerInvariant();
        if (!StackOperations.Actions.Contains(action))
        {
            throw new McpException($"Unknown action '{action}': {string.Join(", ", StackOperations.Actions)}.");
        }

        var stack = await stacks.GetByNameAsync(env.Id, name, ct);
        Operation operation;
        if (stack is null)
        {
            if (!(await containers.ListAsync(env, ct, includeStats: false)).Any(c => c.Project == name))
            {
                throw new McpException($"Stack '{name}' not found on {env.Name}.");
            }

            if (!StackOperations.ProjectActions.Contains(action))
            {
                throw new McpException(StackOperations.NotManaged(name));
            }

            operation = action == "update"
                ? UpdateOperations.ProjectUpdate(operations, env.Id, name, backupFirst)
                : StackOperations.ProjectAction(operations, env.Id, name, action);
        }
        else
        {
            operation = action == "update"
                ? StackOperations.Update(operations, env.Id, stack.Id, name, backupFirst, trigger: "mcp")
                : StackOperations.Action(operations, env.Id, stack.Id, name, action, new DeployRequest(Trigger: DeployTrigger.Mcp, Actor: McpScope.Caller(http).Actor));
        }

        return await McpScope.WaitAsync(operations, operation, waitSeconds, ct);
    }

    [McpServerTool(Name = "stack_service_action", ReadOnly = false), Description("Acts on one service of a managed stack: restart, stop, start, recreate (new containers from the compose file) or scale (to replicas containers).")]
    public static async Task<string> StackServiceAction(EnvironmentService environments, StackService stacks,
        [Description(StackHelp)] string name, [Description("The service of the compose file.")] string service,
        [Description("restart, stop, start, recreate or scale.")] string action,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("scale: the number of containers (0 to 100).")] int? replicas = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var stack = await Managed(stacks, env, name, ct);
        if (!Enum.TryParse<StackCommands.ServiceAction>(action, ignoreCase: true, out var verb) || !Enum.IsDefined(verb))
        {
            throw new McpException("Unknown action: restart, stop, start, recreate or scale.");
        }

        if (verb == StackCommands.ServiceAction.Scale && replicas is not (>= 0 and <= 100))
        {
            throw new McpException("scale needs replicas from 0 to 100.");
        }

        var (ok, output) = await stacks.ServiceActionAsync(stack.Id, service.Trim(), verb, replicas ?? 1, ct);
        return ok ? $"{service}: {verb.ToString().ToLowerInvariant()} done." : throw new McpException(StackOperations.LastLine(output));
    }

    [McpServerTool(Name = "set_stack_image", ReadOnly = false), Description("Makes MatDock run another image for a service of a managed stack than its compose file names (the file stays as it is); applied by the next deploy or update. No image = the compose file's again.")]
    public static async Task<string> SetStackImage(EnvironmentService environments, StackService stacks,
        [Description(StackHelp)] string name, [Description("The service of the compose file.")] string service,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("The image, e.g. nginx:1.27.3; left out = the compose file's image.")] string? image = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var stack = await Managed(stacks, env, name, ct);
        var (ok, message) = await stacks.SetImageOverrideAsync(stack.Id, service.Trim(), image, ct);
        return ok ? message : throw new McpException(message);
    }

    [McpServerTool(Name = "remove_stack", ReadOnly = false, Destructive = true), Description("Takes a managed stack down (docker compose down) and removes it from MatDock. With removeVolumes its volumes and all their data go too. Cannot be undone; a bundle backup first (backup_stack) is the safe way.")]
    public static async Task<object> RemoveStack(EnvironmentService environments, StackService stacks, OperationRegistry operations,
        [Description(StackHelp)] string name, [Description(EnvironmentHelp)] string? environment = null,
        [Description("Also remove the volumes of the stack.")] bool removeVolumes = false,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var stack = await Managed(stacks, env, name, ct);
        return await McpScope.WaitAsync(operations, StackOperations.Delete(operations, env.Id, stack.Id, name, removeVolumes), waitSeconds, ct);
    }

    private static async Task<Stack> Managed(StackService stacks, DockerEnvironment env, string name, CancellationToken ct)
    {
        Valid(name);
        return await stacks.GetByNameAsync(env.Id, name, ct) ?? throw new McpException($"Stack '{name}' is not managed by MatDock on {env.Name}.");
    }

    private static void Valid(string name)
    {
        if (!StackCommands.IsValidName(name))
        {
            throw new McpException($"'{name}' is no stack name (lowercase a-z, 0-9, _ and -, starting with a letter or digit).");
        }
    }
}
