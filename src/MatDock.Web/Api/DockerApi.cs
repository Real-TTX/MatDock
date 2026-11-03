using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;

namespace MatDock.Web.Api;

/// <summary>Body of <c>POST /api/v1/images/pull</c>.</summary>
public sealed class PullImageBody
{
    public string? Reference { get; set; }
}

/// <summary>
/// <c>/api/v1</c> for the engine itself: the environments, and the containers, images, volumes, networks and events of one environment
/// (<c>?environment=</c> id or name, the local one without it). The same services as the pages; what a page refuses, this refuses too.
/// </summary>
public static class DockerApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/environments", ListEnvironmentsAsync).WithSummary("The environments with their state and their last reading.");

        api.MapGet("/containers", ListContainersAsync).WithSummary("The containers of an environment with CPU and memory.");
        api.MapGet("/containers/{name}", GetContainerAsync).WithSummary("One container as docker inspect says (secrets of its environment masked).");
        api.MapPost("/containers/{name}/actions/{action}", ContainerActionAsync).WithSummary("start, stop, restart, pause, unpause or kill a container.");
        api.MapDelete("/containers/{name}", RemoveContainerAsync).WithSummary("Removes a container (force: a running one; volumes: its anonymous volumes).");
        api.MapGet("/containers/{name}/logs", ContainerLogsAsync).WithSummary("The last lines of the log of a container (tail, at most 5000).");

        api.MapGet("/images", ListImagesAsync).WithSummary("The images of an environment and the containers that use them.");
        api.MapPost("/images/pull", PullImageAsync).WithSummary("Pulls an image.");
        api.MapDelete("/images", RemoveImageAsync).WithSummary("Removes an image (?reference=; force: also when a stopped container uses it).");

        api.MapGet("/volumes", ListVolumesAsync).WithSummary("The volumes of an environment and whether a container uses them.");
        api.MapDelete("/volumes/{name}", RemoveVolumeAsync).WithSummary("Removes a volume no container uses.");

        api.MapGet("/networks", ListNetworksAsync).WithSummary("The networks of an environment.");
        api.MapDelete("/networks/{name}", RemoveNetworkAsync).WithSummary("Removes a network no container uses.");

        api.MapGet("/events", ListEventsAsync).WithSummary("The events of the engine of the last hours (hours 1-168, type, container).");
    }

    private static async Task<IResult> ListEnvironmentsAsync(HttpContext http, EnvironmentService environments, MetricsSnapshot snapshot)
    {
        var readings = snapshot.All.ToDictionary(r => r.Environment.Id);
        return ApiResults.Ok((await environments.GetAllAsync(http.RequestAborted)).Select(e => new
        {
            id = e.Id,
            name = e.Name,
            type = MatDock.Core.Environments.ConnectionNames.Of(e.ConnectionType),
            host = e.ConnectionType == ConnectionType.Local ? null : e.Host,
            status = e.Status.ToString().ToLowerInvariant(),
            enabled = e.IsEnabled,
            dockerVersion = e.DockerVersion,
            lastCheckedAt = ApiResults.Utc(e.LastCheckedAt),
            reading = readings.TryGetValue(e.Id, out var r)
                ? new { at = ApiResults.Utc(r.TimeUtc), cpuPercent = Math.Round(r.Host.CpuPercent, 1), memoryPercent = Math.Round(r.Host.MemPercent, 1), diskPercent = Math.Round(r.Host.DiskPercent, 1), containersRunning = r.Containers.Count(c => c.IsRunning) }
                : null,
        }));
    }

    private static async Task<IResult> ListContainersAsync(HttpContext http, EnvironmentService environments, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        return ApiResults.Ok((await containers.ListAsync(env, http.RequestAborted)).Select(c => new
        {
            id = c.Id, name = c.Name, image = c.Image, state = c.State, status = c.Status, stack = c.Project, service = c.Service, ports = c.Ports,
            cpuPercent = c.CpuPercent, memoryPercent = c.MemPercent, memoryBytes = MemoryText.UsedBytes(c.MemUsage),
        }));
    }

    private static async Task<IResult> GetContainerAsync(HttpContext http, string name, EnvironmentService environments, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        if (!ContainerCommands.IsValidId(name))
        {
            return ApiResults.BadRequest("Invalid container name or id.");
        }

        var inspect = await containers.InspectAsync(env, name, http.RequestAborted);
        return inspect is null
            ? ApiResults.NotFound($"Container '{name}' not found on {env.Name}.")
            : ApiResults.Ok(new
            {
                inspect.Id, inspect.Name, inspect.Image, inspect.Status, inspect.Running, inspect.HealthStatus, inspect.ExitCode, inspect.RestartCount, inspect.RestartPolicy,
                stack = inspect.Project, created = inspect.Created, startedAt = inspect.StartedAt, finishedAt = inspect.FinishedAt, ports = inspect.PortBindings,
                environment = inspect.Env.Select(e => new { e.Name, e.Value, e.Masked }), labels = inspect.Labels,
                networks = inspect.Networks.Select(n => new { n.Name, n.IpAddress, n.Aliases }), mounts = inspect.Mounts.Select(m => new { m.Type, m.Name, m.Source, m.Destination, m.ReadWrite }),
            });
    }

    private static async Task<IResult> ContainerActionAsync(HttpContext http, string name, string action, EnvironmentService environments, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        if (!Enum.TryParse<ContainerAction>(action, ignoreCase: true, out var verb) || !Enum.IsDefined(verb))
        {
            return ApiResults.BadRequest("Unknown action: start, stop, restart, pause, unpause or kill.");
        }

        var (ok, message) = await containers.ActionAsync(env, name, verb, http.RequestAborted);
        return ok ? ApiResults.Ok(new { ok, message }) : ApiResults.BadGateway(message);
    }

    private static async Task<IResult> RemoveContainerAsync(HttpContext http, string name, bool force, bool volumes, EnvironmentService environments, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (ok, message) = await containers.RemoveAsync(env, name, force, volumes, http.RequestAborted);
        return ok ? ApiResults.Ok(new { ok, message }) : ApiResults.Conflict(message);
    }

    private static async Task<IResult> ContainerLogsAsync(HttpContext http, string name, int? tail, EnvironmentService environments, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        if (!ContainerCommands.IsValidId(name))
        {
            return ApiResults.BadRequest("Invalid container name or id.");
        }

        return Results.Text(await containers.LogsAsync(env, name, Math.Clamp(tail ?? 200, 1, 5000), http.RequestAborted), "text/plain; charset=utf-8");
    }

    private static async Task<IResult> ListImagesAsync(HttpContext http, EnvironmentService environments, IEnvironmentConnectionService connection, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var images = await connection.ListImagesAsync(environments.BuildSettings(env), http.RequestAborted);
        var running = await containers.ListAsync(env, http.RequestAborted, includeStats: false);
        return ApiResults.Ok(images.Select(i => new
        {
            id = i.Id, reference = i.Reference, repository = i.Repository, tag = i.Tag, digest = i.Digest, size = i.Size, created = i.CreatedAt, dangling = i.Dangling,
            usedBy = running.Where(c => c.Image == i.Reference || c.Image == i.Id).Select(c => c.Name),
        }));
    }

    private static async Task<IResult> PullImageAsync(HttpContext http, EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (body, bodyError) = await ApiResults.ReadBodyAsync<PullImageBody>(http.Request);
        if (body is null || string.IsNullOrWhiteSpace(body.Reference))
        {
            return bodyError ?? ApiResults.BadRequest("The body names the image: { \"reference\": \"nginx:1.27\" }.");
        }

        var (ok, message) = await connection.PullImageAsync(environments.BuildSettings(env), body.Reference.Trim(), http.RequestAborted);
        return ok ? ApiResults.Ok(new { ok, message }) : ApiResults.BadGateway(message);
    }

    private static async Task<IResult> RemoveImageAsync(HttpContext http, string? reference, bool force, EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return ApiResults.BadRequest("Name the image: ?reference=nginx:1.27");
        }

        var (ok, message) = await connection.RemoveImageAsync(environments.BuildSettings(env), reference.Trim(), force, http.RequestAborted);
        return ok ? ApiResults.Ok(new { ok, message }) : ApiResults.Conflict(message);
    }

    private static async Task<IResult> ListVolumesAsync(HttpContext http, EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (volumes, unused) = await connection.ListVolumesWithUsageAsync(environments.BuildSettings(env), http.RequestAborted);
        return ApiResults.Ok(volumes.Select(v => new { name = v.Name, driver = v.Driver, mountpoint = v.Mountpoint, inUse = !unused.Contains(v.Name) }));
    }

    private static async Task<IResult> RemoveVolumeAsync(HttpContext http, string name, EnvironmentService environments, IEnvironmentConnectionService connection, ContainerService containers)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        // As on the page: a volume a container mounts is never removed (docker would refuse anyway).
        var users = await containers.ListByVolumeAsync(env, name, http.RequestAborted);
        if (users.Count > 0)
        {
            return ApiResults.Conflict($"Volume '{name}' is used by {string.Join(", ", users.Select(c => c.Name))}.");
        }

        var result = await connection.RemoveVolumesAsync(environments.BuildSettings(env), [name], http.RequestAborted);
        return result.Success ? ApiResults.Ok(new { ok = true, message = $"Volume '{name}' removed." }) : ApiResults.Conflict(result.Detail);
    }

    private static async Task<IResult> ListNetworksAsync(HttpContext http, EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (networks, unused) = await connection.ListNetworksWithUsageAsync(environments.BuildSettings(env), http.RequestAborted);
        return ApiResults.Ok(networks.Select(n => new { id = n.Id, name = n.Name, driver = n.Driver, scope = n.Scope, @internal = n.Internal, predefined = n.IsPredefined, inUse = !unused.Contains(n.Name) }));
    }

    private static async Task<IResult> RemoveNetworkAsync(HttpContext http, string name, EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (ok, message) = await connection.RemoveNetworkAsync(environments.BuildSettings(env), name, http.RequestAborted);
        return ok ? ApiResults.Ok(new { ok, message }) : ApiResults.Conflict(message);
    }

    private static async Task<IResult> ListEventsAsync(HttpContext http, int? hours, string? type, string? container, EnvironmentService environments, DockerSystemService system)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var (events, problem) = await system.GetEventsAsync(env, Math.Clamp(hours ?? 24, 1, 168), type, null, container, http.RequestAborted);
        return problem is not null
            ? ApiResults.BadGateway(problem)
            : ApiResults.Ok(events.Select(e => new { time = ApiResults.Utc(e.TimeUtc), e.Type, e.Action, e.Id, e.Name, problem = e.IsProblem, attributes = e.Attributes }));
    }
}
