using System.ComponentModel;
using MatDock.Core;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>The hosts as a whole: status, what an engine says about itself, its events, the readings over time, a cleanup.</summary>
[McpServerToolType]
public static class EngineTools
{
    [McpServerTool(Name = "get_status", ReadOnly = true), Description("Start here. The MatDock instance and its environments (Docker hosts): id, name, connection (local, ssh, tcp), status, Docker version, the last reading (CPU, memory, disk in percent, running containers) and the number of managed stacks per environment.")]
    public static async Task<object> GetStatus(EnvironmentService environments, StackService stacks, MetricsSnapshot snapshot, CancellationToken ct)
    {
        var readings = snapshot.All.ToDictionary(r => r.Environment.Id);
        var managed = (await stacks.GetAllAsync(ct)).GroupBy(s => s.EnvironmentId).ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            app = "matdock",
            version = AppVersion.Informational,
            environments = (await environments.GetAllAsync(ct)).Select(e => new
            {
                id = e.Id,
                name = e.Name,
                connection = MatDock.Core.Environments.ConnectionNames.Of(e.ConnectionType),
                host = e.ConnectionType == ConnectionType.Local ? null : e.Host,
                status = e.Status.ToString().ToLowerInvariant(),
                enabled = e.IsEnabled,
                dockerVersion = e.DockerVersion,
                managedStacks = managed.GetValueOrDefault(e.Id),
                reading = readings.TryGetValue(e.Id, out var r)
                    ? new { at = r.TimeUtc, cpuPercent = Math.Round(r.Host.CpuPercent, 1), memoryPercent = Math.Round(r.Host.MemPercent, 1), diskPercent = Math.Round(r.Host.DiskPercent, 1), containersRunning = r.Containers.Count(c => c.IsRunning) }
                    : null,
            }),
        };
    }

    [McpServerTool(Name = "get_engine_info", ReadOnly = true), Description("What the Docker engine of an environment says about itself (docker info, docker version, compose version) and its disk usage per kind (images, containers, volumes, build cache) with what could be reclaimed.")]
    public static async Task<object> GetEngineInfo(EnvironmentService environments, DockerSystemService system,
        [Description("The environment: id or name (may be left out when there is only one).")] string? environment = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var o = await system.GetOverviewAsync(env, ct);
        return new
        {
            environment = env.Name,
            o.ServerVersion, o.ApiVersion, o.ComposeVersion, o.HostName, o.OperatingSystem, o.KernelVersion, o.Architecture, o.Cpus, o.MemoryBytes,
            o.StorageDriver, o.DockerRootDir, o.LoggingDriver, o.CgroupDriver, o.CgroupVersion, o.DefaultRuntime, o.Runtimes, o.SecurityOptions,
            o.Containers, o.ContainersRunning, o.ContainersPaused, o.ContainersStopped, o.Images, o.SwarmState, o.Warnings,
            diskUsage = o.DiskUsage.Select(d => new { d.Type, d.TotalCount, d.Active, d.Size, d.Reclaimable }),
            errors = o.Errors,
        };
    }

    [McpServerTool(Name = "list_events", ReadOnly = true), Description("What happened on the engine of an environment (docker events), newest first: containers that started, died (with exit code) or turned unhealthy, images pulled, volumes and networks made or removed. Docker keeps only its newest 256 events. The probes of health checks (exec_*) are left out unless includeProbes is true.")]
    public static async Task<object> ListEvents(EnvironmentService environments, DockerSystemService system,
        [Description("The environment: id or name (may be left out when there is only one).")] string? environment = null,
        [Description("How many hours back (1 to 168, default 24).")] int? hours = null,
        [Description("Only one type: container, image, volume, network, daemon or plugin.")] string? type = null,
        [Description("Only the events of this container (name).")] string? container = null,
        [Description("Only problems: an error exit, out of memory, unhealthy.")] bool problemsOnly = false,
        [Description("Also the probes of health checks.")] bool includeProbes = false,
        [Description("At most this many events (default 100).")] int? take = null,
        CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (events, error) = await system.GetEventsAsync(env, Math.Clamp(hours ?? 24, 1, 168), type, null, container, ct);
        if (error is not null)
        {
            throw new McpException($"Docker on '{env.Name}' did not answer: {error}");
        }

        return events
            .Where(e => problemsOnly ? e.IsProblem : includeProbes || !e.IsProbe)
            .Take(Math.Clamp(take ?? 100, 1, 1000))
            .Select(e => new { time = e.TimeUtc, e.Type, action = e.Label, e.Name, e.Image, exitCode = e.ExitCode, problem = e.IsProblem });
    }

    [McpServerTool(Name = "get_metrics", ReadOnly = true), Description("The readings of a host or of one container over a range (taken every 5 minutes, hourly after two days, kept 30 days): CPU and memory (and disk for a host) in percent as now, average and peak, plus up to 48 points of the course.")]
    public static async Task<object> GetMetrics(EnvironmentService environments, MetricsStore metrics,
        [Description("The environment: id or name (may be left out when there is only one).")] string? environment = null,
        [Description("A container name; without it the host.")] string? container = null,
        [Description("6h, 24h (default), 7d or 30d.")] string? range = null,
        CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var key = MetricsStore.Ranges.ContainsKey(range ?? string.Empty) ? range! : "24h";
        var points = await metrics.GetAsync(env.Id, string.IsNullOrWhiteSpace(container) ? null : container.Trim(), MetricsStore.RangeOf(key), DateTime.UtcNow, ct);
        if (points.Count == 0)
        {
            return new { environment = env.Name, container, range = key, points = 0, note = "No readings in this range." };
        }

        object Stat(IReadOnlyList<double> v) => new { now = Math.Round(v[^1], 1), avg = Math.Round(v.Average(), 1), max = Math.Round(v.Max(), 1) };
        var step = Math.Max(1, points.Count / 48);
        var disks = points.Where(p => p.Disk is not null).Select(p => p.Disk!.Value).ToList();
        return new
        {
            environment = env.Name,
            container,
            range = key,
            from = points[0].TimeUtc,
            readings = points.Count,
            cpuPercent = Stat(points.Select(p => p.Cpu).ToList()),
            memoryPercent = Stat(points.Select(p => p.Mem).ToList()),
            diskPercent = disks.Count > 0 ? Stat(disks) : null,
            memoryBytesNow = points[^1].MemBytes,
            course = points.Where((_, i) => i % step == 0).Select(p => new { t = p.TimeUtc, cpu = Math.Round(p.Cpu, 1), mem = Math.Round(p.Mem, 1) }),
        };
    }

    [McpServerTool(Name = "test_environment", ReadOnly = true), Description("Connects to an environment again and refreshes its status (online, offline, error) and Docker version.")]
    public static async Task<object> TestEnvironment(EnvironmentService environments,
        [Description("The environment: id or name.")] string environment, CancellationToken ct = default)
    {
        var all = await environments.GetAllAsync(ct);
        var env = (long.TryParse(environment, out var id) ? all.FirstOrDefault(e => e.Id == id) : null)
                  ?? all.FirstOrDefault(e => string.Equals(e.Name, environment.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new McpException($"Environment '{environment}' not found.");
        var result = await environments.TestAndPersistAsync(env.Id, ct);
        return new { environment = env.Name, ok = result.Success, status = result.Status.ToString().ToLowerInvariant(), result.Message, result.DockerVersion };
    }

    [McpServerTool(Name = "set_environment_enabled", ReadOnly = false), Description("Enables or disables an environment. A disabled one is skipped by schedules and automation and hidden from the lists; its settings stay.")]
    public static async Task<string> SetEnvironmentEnabled(EnvironmentService environments,
        [Description("The environment: id or name.")] string environment,
        [Description("true = enabled, false = disabled.")] bool enabled, CancellationToken ct = default)
    {
        var all = await environments.GetAllAsync(ct);
        var env = (long.TryParse(environment, out var id) ? all.FirstOrDefault(e => e.Id == id) : null)
                  ?? all.FirstOrDefault(e => string.Equals(e.Name, environment.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new McpException($"Environment '{environment}' not found.");
        await environments.SetEnabledAsync(env.Id, enabled, ct);
        return $"Environment '{env.Name}' is {(enabled ? "enabled" : "disabled")}.";
    }

    [McpServerTool(Name = "cleanup_engine", ReadOnly = false, Destructive = true), Description("Removes what nothing uses on an environment, step by step (each step says what it freed). Never touches volumes. Cannot be undone.")]
    public static async Task<object> CleanupEngine(EnvironmentService environments, DockerSystemService system,
        [Description("The environment: id or name (may be left out when there is only one).")] string? environment = null,
        [Description("Stopped containers.")] bool stoppedContainers = false,
        [Description("Dangling images (without a tag).")] bool danglingImages = false,
        [Description("Every image no container uses (they are pulled again when needed).")] bool allUnusedImages = false,
        [Description("Networks no container uses.")] bool unusedNetworks = false,
        [Description("The build cache.")] bool buildCache = false,
        CancellationToken ct = default)
    {
        if (!(stoppedContainers || danglingImages || allUnusedImages || unusedNetworks || buildCache))
        {
            throw new McpException("Choose at least one thing to clean up.");
        }

        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        var (ok, lines) = await system.CleanupAsync(env, new SystemCleanup(stoppedContainers, danglingImages, allUnusedImages, unusedNetworks, buildCache), ct);
        return new { environment = env.Name, ok, steps = lines };
    }
}
