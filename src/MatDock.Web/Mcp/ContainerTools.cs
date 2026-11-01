using System.ComponentModel;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>The containers of an environment: list, look inside, logs, actions, run, change, exec.</summary>
[McpServerToolType]
public static class ContainerTools
{
    private const string EnvironmentHelp = "The environment: id or name (may be left out when there is only one).";
    private const string ContainerHelp = "The container: its name or id.";

    [McpServerTool(Name = "list_containers", ReadOnly = true), Description("The containers of an environment: id, name, image, state, status, compose stack and service, ports, CPU and memory.")]
    public static async Task<object> ListContainers(EnvironmentService environments, ContainerService containers,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("running, stopped or all (default).")] string? state = null,
        [Description("Only those whose name, image or stack contains this text.")] string? search = null,
        CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var list = await containers.ListAsync(env, ct);
        return list
            .Where(c => state?.ToLowerInvariant() switch { "running" => c.IsRunning, "stopped" => !c.IsRunning, _ => true })
            .Where(c => string.IsNullOrWhiteSpace(search) || new[] { c.Name, c.Image, c.Project }.Any(v => v?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true))
            .Select(c => new
            {
                c.Id, c.Name, c.Image, c.State, c.Status, stack = c.Project, c.Service, c.Ports,
                cpuPercent = c.CpuPercent, memoryPercent = c.MemPercent, memoryBytes = MemoryText.UsedBytes(c.MemUsage),
            });
    }

    [McpServerTool(Name = "inspect_container", ReadOnly = true), Description("One container in detail (docker inspect): state, exit code, restarts, restart policy, health with its last checks, command, environment variables (values of secrets masked), labels, ports, networks with addresses, mounts, limits.")]
    public static async Task<object> InspectContainer(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(container);
        var i = await containers.InspectAsync(env, container, ct) ?? throw new McpException($"Container '{container}' not found on {env.Name}.");
        return new
        {
            i.Id, i.Name, i.Image, i.Status, i.Running, i.Paused, i.Restarting, i.OomKilled, i.ExitCode, i.StartedAt, i.FinishedAt, i.StateError,
            i.RestartCount, i.RestartPolicy, i.HealthStatus, i.HealthFailingStreak,
            healthLog = i.HealthLog.Select(h => new { h.Start, h.ExitCode, h.Output }),
            i.Entrypoint, i.Cmd, i.WorkingDir, i.User, i.Hostname, stack = i.Project,
            environment = i.Env.Select(e => $"{e.Name}={e.Value}"),
            i.Labels, ports = i.PortBindings,
            networks = i.Networks.Select(n => new { n.Name, n.IpAddress, n.Gateway, n.Aliases }),
            mounts = i.Mounts.Select(m => new { m.Type, m.Name, m.Source, m.Destination, m.ReadWrite }),
            i.MemoryBytes, i.Cpus, i.Privileged, i.CapAdd, i.CapDrop, i.Devices, i.LogDriver,
        };
    }

    [McpServerTool(Name = "container_logs", ReadOnly = true), Description("The last lines of the log of a container (stdout and stderr).")]
    public static async Task<string> ContainerLogs(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container, [Description(EnvironmentHelp)] string? environment = null,
        [Description("How many lines (1 to 5000, default 200).")] int? tail = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(container);
        return McpScope.Tail(await containers.LogsAsync(env, container, Math.Clamp(tail ?? 200, 1, 5000), ct));
    }

    [McpServerTool(Name = "container_processes", ReadOnly = true), Description("The processes running in a container (docker top).")]
    public static async Task<object> ContainerProcesses(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container, [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(container);
        var (table, error) = await containers.TopAsync(env, container, ct);
        return error is not null ? throw new McpException(error) : new { table.Columns, table.Rows };
    }

    [McpServerTool(Name = "container_action", ReadOnly = false), Description("start, stop, restart, pause, unpause or kill a container.")]
    public static async Task<string> ContainerAction(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container,
        [Description("start, stop, restart, pause, unpause or kill.")] string action,
        [Description(EnvironmentHelp)] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        if (!Enum.TryParse<ContainerAction>(action, ignoreCase: true, out var verb) || !Enum.IsDefined(verb))
        {
            throw new McpException("Unknown action: start, stop, restart, pause, unpause or kill.");
        }

        var (ok, message) = await containers.ActionAsync(env, container, verb, ct);
        return ok ? $"{verb} {container} on {env.Name}: {message}" : throw new McpException(message);
    }

    [McpServerTool(Name = "remove_container", ReadOnly = false, Destructive = true), Description("Removes a container. A running one only with force (it is stopped first). Its anonymous volumes go too only with removeAnonymousVolumes; named volumes always stay. A container of a compose stack comes back with the next deploy.")]
    public static async Task<string> RemoveContainer(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("Also when it runs.")] bool force = false,
        [Description("Also its anonymous volumes.")] bool removeAnonymousVolumes = false, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, message) = await containers.RemoveAsync(env, container, force, removeAnonymousVolumes, ct);
        return ok ? $"Removed {container} from {env.Name}." : throw new McpException(message);
    }

    [McpServerTool(Name = "run_container", ReadOnly = false), Description("Starts a new container that MatDock does not manage as a stack (docker run -d). For anything that should stay and be updated, a stack is the better way (deploy_stack).")]
    public static async Task<string> RunContainer(EnvironmentService environments, ContainerService containers,
        [Description("The image, e.g. nginx:1.27.")] string image,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("The name of the container.")] string? name = null,
        [Description("Ports as host:container[/proto], e.g. 8080:80.")] string[]? ports = null,
        [Description("Environment variables as KEY=value.")] string[]? env = null,
        [Description("Volumes or binds as source:/path[:ro].")] string[]? volumes = null,
        [Description("no (default), always, unless-stopped or on-failure.")] string? restart = null,
        [Description("A network to attach it to.")] string? network = null,
        [Description("The command and its arguments.")] string[]? command = null,
        [Description("Labels as key=value.")] string[]? labels = null,
        [Description("A memory limit, e.g. 512m.")] string? memory = null,
        [Description("A CPU limit, e.g. 1.5.")] string? cpus = null,
        CancellationToken ct = default)
    {
        var target = await McpScope.EnvironmentAsync(environments, environment, ct);
        var spec = new ContainerRunSpec
        {
            Image = image.Trim(), Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(), Ports = [.. ports ?? []], Env = [.. env ?? []], Volumes = [.. volumes ?? []],
            Restart = string.IsNullOrWhiteSpace(restart) ? "no" : restart.Trim(), Network = string.IsNullOrWhiteSpace(network) ? null : network.Trim(),
            Command = [.. command ?? []], Labels = [.. labels ?? []], Memory = memory, Cpus = cpus,
        };
        if (ContainerRunCommands.Validate(spec) is { } problem)
        {
            throw new McpException(problem);
        }

        var (ok, message) = await containers.RunAsync(target, spec, ct);
        return ok ? $"Started on {target.Name}: {message}" : throw new McpException(message);
    }

    [McpServerTool(Name = "update_container", ReadOnly = false), Description("Changes the memory limit, the CPU limit or the restart policy of a container while it runs (docker update).")]
    public static async Task<string> UpdateContainer(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("A memory limit, e.g. 512m (0 = none).")] string? memory = null,
        [Description("A CPU limit, e.g. 1.5 (0 = none).")] string? cpus = null,
        [Description("no, always, unless-stopped or on-failure.")] string? restart = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, message) = await containers.UpdateAsync(env, container, new ContainerUpdateSpec(memory, cpus, restart), ct);
        return ok ? $"Updated {container}: {message}" : throw new McpException(message);
    }

    [McpServerTool(Name = "recreate_container", ReadOnly = false), Description("Re-creates a container that is not part of a compose stack with the same settings and volumes, pulling its image first (pull=false keeps the local one). It is stopped for a moment; on a failure the old one is put back.")]
    public static async Task<string> RecreateContainer(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("Pull the newest image of its tag first (default true).")] bool pull = true, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var log = new List<string>();
        var (ok, message) = await containers.RecreateAsync(env, container, pull, log.Add, ct);
        return ok ? string.Join("\n", log.Append(message)) : throw new McpException(string.Join("\n", log.Append(message)));
    }

    [McpServerTool(Name = "exec_in_container", ReadOnly = false, Destructive = true), Description("Runs a shell command in a running container (docker exec … sh -c) and answers its output and exit code. As powerful as a shell in the container: say what it does before running a command that changes something.")]
    public static async Task<object> ExecInContainer(EnvironmentService environments, ContainerService containers,
        [Description(ContainerHelp)] string container,
        [Description("The command for sh -c, e.g. 'ls -la /data'.")] string command,
        [Description(EnvironmentHelp)] string? environment = null,
        [Description("Run as this user (name or uid[:gid]).")] string? user = null,
        [Description("Seconds before it is stopped (1 to 600, default 60).")] int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        Valid(container);
        string Command(string head)
        {
            try
            {
                return ContainerCommands.Exec(head, container, command, user);
            }
            catch (ArgumentException ex)
            {
                throw new McpException(ex.Message);
            }
        }

        var (ok, output) = await containers.RunAsync(env, Command, ct, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 60, 1, 600)));
        return new { ok, output = McpScope.Tail(output) };
    }

    private static void Valid(string container)
    {
        if (!ContainerCommands.IsValidId(container))
        {
            throw new McpException($"'{container}' is no container name or id.");
        }
    }
}
