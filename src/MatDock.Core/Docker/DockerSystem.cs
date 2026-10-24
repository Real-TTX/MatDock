using System.Text.Json;
using System.Text.Json.Nodes;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Images;
using MatDock.Core.Networks;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Docker;

/// <summary>The commands about the Docker engine of a host as a whole.</summary>
public static class SystemCommands
{
    public static string Info(string dockerHead) => VolumeCommands.PathPrefix + dockerHead + " info --format '{{json .}}'";

    public static string Version(string dockerHead) => VolumeCommands.PathPrefix + dockerHead + " version --format '{{json .}}'";

    public static string ComposeVersion(string dockerHead) => VolumeCommands.PathPrefix + dockerHead + " compose version --short 2>/dev/null";

    /// <summary>The space per kind (images, containers, volumes, build cache), one JSON line each.</summary>
    public static string DiskUsage(string dockerHead) => VolumeCommands.PathPrefix + dockerHead + " system df --format '{{json .}}'";

    /// <summary>Removes the build cache that no build uses (<c>docker builder prune</c>).</summary>
    public static string BuilderPrune(string dockerHead) => VolumeCommands.PathPrefix + dockerHead + " builder prune -f 2>&1";
}

/// <summary>One kind of the disk usage of an engine (<c>docker system df</c>).</summary>
public sealed record DiskUsageRow(string Type, string TotalCount, string Active, string Size, string Reclaimable);

/// <summary>What the engine of a host says about itself: <c>docker info</c>, <c>docker version</c>, the compose plugin and the disk usage.</summary>
public sealed class DockerSystemOverview
{
    public string? ServerVersion { get; init; }
    public string? ApiVersion { get; init; }
    public string? ClientVersion { get; init; }
    public string? ComposeVersion { get; init; }
    public string? HostName { get; init; }
    public string? OperatingSystem { get; init; }
    public string? KernelVersion { get; init; }
    public string? Architecture { get; init; }
    public int Cpus { get; init; }
    public long MemoryBytes { get; init; }
    public string? StorageDriver { get; init; }
    public string? DockerRootDir { get; init; }
    public string? LoggingDriver { get; init; }
    public string? CgroupDriver { get; init; }
    public string? CgroupVersion { get; init; }
    public string? DefaultRuntime { get; init; }
    public IReadOnlyList<string> Runtimes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SecurityOptions { get; init; } = Array.Empty<string>();
    public bool Rootless => SecurityOptions.Any(o => o.Contains("rootless", StringComparison.OrdinalIgnoreCase));
    public int Containers { get; init; }
    public int ContainersRunning { get; init; }
    public int ContainersPaused { get; init; }
    public int ContainersStopped { get; init; }
    public int Images { get; init; }
    public string? SwarmState { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DiskUsageRow> DiskUsage { get; init; } = Array.Empty<DiskUsageRow>();

    /// <summary>What could not be read (the engine is reachable but one question failed).</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static DockerSystemOverview Parse(string? infoJson, string? versionJson, string? composeVersion, string? diskUsage, IReadOnlyList<string> errors)
    {
        var info = TryParse(infoJson) as JsonObject;
        var version = TryParse(versionJson) as JsonObject;
        return new DockerSystemOverview
        {
            ServerVersion = Text(info?["ServerVersion"]) ?? Text(version?["Server"]?["Version"]),
            ApiVersion = Text(version?["Server"]?["ApiVersion"]),
            ClientVersion = Text(version?["Client"]?["Version"]),
            ComposeVersion = string.IsNullOrWhiteSpace(composeVersion) ? null : composeVersion.Trim(),
            HostName = Text(info?["Name"]),
            OperatingSystem = Text(info?["OperatingSystem"]),
            KernelVersion = Text(info?["KernelVersion"]),
            Architecture = Text(info?["Architecture"]),
            Cpus = Int(info?["NCPU"]),
            MemoryBytes = Long(info?["MemTotal"]),
            StorageDriver = Text(info?["Driver"]),
            DockerRootDir = Text(info?["DockerRootDir"]),
            LoggingDriver = Text(info?["LoggingDriver"]),
            CgroupDriver = Text(info?["CgroupDriver"]),
            CgroupVersion = Text(info?["CgroupVersion"]),
            DefaultRuntime = Text(info?["DefaultRuntime"]),
            Runtimes = (info?["Runtimes"] as JsonObject)?.Select(r => r.Key).OrderBy(r => r, StringComparer.Ordinal).ToList() ?? new List<string>(),
            SecurityOptions = (info?["SecurityOptions"] as JsonArray)?.Select(o => Text(o) ?? string.Empty).Where(o => o.Length > 0)
                .Select(o => o.StartsWith("name=", StringComparison.Ordinal) ? o[5..].Split(',')[0] : o).ToList() ?? new List<string>(),
            Containers = Int(info?["Containers"]),
            ContainersRunning = Int(info?["ContainersRunning"]),
            ContainersPaused = Int(info?["ContainersPaused"]),
            ContainersStopped = Int(info?["ContainersStopped"]),
            Images = Int(info?["Images"]),
            SwarmState = Text(info?["Swarm"]?["LocalNodeState"]),
            Warnings = (info?["Warnings"] as JsonArray)?.Select(w => Text(w) ?? string.Empty).Where(w => w.Length > 0).ToList() ?? new List<string>(),
            DiskUsage = ParseDiskUsage(diskUsage),
            Errors = errors,
        };
    }

    public static IReadOnlyList<DiskUsageRow> ParseDiskUsage(string? output)
    {
        var rows = new List<DiskUsageRow>();
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(line) is JsonObject row)
            {
                rows.Add(new DiskUsageRow(Text(row["Type"]) ?? "?", Text(row["TotalCount"]) ?? "0", Text(row["Active"]) ?? "0",
                    Text(row["Size"]) ?? "0B", Text(row["Reclaimable"]) ?? "0B"));
            }
        }

        return rows;
    }

    private static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
}

/// <summary>What a system cleanup removes; volumes are not part of it (they hold data: the Volumes page has its own prune with a preview).</summary>
public sealed record SystemCleanup(bool StoppedContainers, bool DanglingImages, bool AllUnusedImages, bool UnusedNetworks, bool BuildCache);

/// <summary>The engine of a host as a whole: what it says about itself, and the cleanup of what nothing uses.</summary>
public sealed class DockerSystemService
{
    private readonly EnvironmentService _environments;
    private readonly ContainerService _containers;
    private readonly ILogger<DockerSystemService> _logger;

    public DockerSystemService(EnvironmentService environments, ContainerService containers, ILogger<DockerSystemService> logger)
    {
        _environments = environments;
        _containers = containers;
        _logger = logger;
    }

    /// <summary>Asks the engine about itself; a question that fails is named in <see cref="DockerSystemOverview.Errors"/> instead of failing the whole page.</summary>
    public async Task<DockerSystemOverview> GetOverviewAsync(DockerEnvironment environment, CancellationToken ct = default)
    {
        var errors = new List<string>();

        async Task<string?> Ask(string what, Func<string, string> command)
        {
            try
            {
                var (ok, output) = await _containers.RunAsync(environment, command, ct);
                if (ok)
                {
                    return output;
                }

                errors.Add($"{what}: {DockerErrorMessages.InterpretDockerError(output, output)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{What} on env {Env} failed.", what, environment.Id);
                errors.Add($"{what}: {ex.Message}");
            }

            return null;
        }

        var info = await Ask("docker info", SystemCommands.Info);
        var version = await Ask("docker version", SystemCommands.Version);
        var compose = await Ask("docker compose version", SystemCommands.ComposeVersion);
        var df = await Ask("docker system df", SystemCommands.DiskUsage);
        return DockerSystemOverview.Parse(info, version, compose, df, errors.Distinct().ToList());
    }

    /// <summary>Removes what the cleanup names, one step after the other; each step says what it did (a failed step does not stop the next).</summary>
    public async Task<(bool Ok, IReadOnlyList<string> Lines)> CleanupAsync(DockerEnvironment environment, SystemCleanup cleanup, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var ok = true;

        async Task Step(string what, Func<string, string> command)
        {
            try
            {
                var (done, output) = await _containers.RunAsync(environment, command, ct);
                ok &= done;
                var reclaimed = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("Total", StringComparison.OrdinalIgnoreCase));
                lines.Add(done ? $"{what}: done.{(reclaimed is null ? string.Empty : " " + reclaimed + ".")}" : $"{what}: {DockerErrorMessages.InterpretDockerError(output, output)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ok = false;
                lines.Add($"{what}: {ex.Message}");
            }
        }

        if (cleanup.StoppedContainers)
        {
            await Step("Stopped containers", ContainerCommands.PruneStopped);
        }

        if (cleanup.AllUnusedImages || cleanup.DanglingImages)
        {
            await Step(cleanup.AllUnusedImages ? "Unused images" : "Dangling images", head => ImageCommands.Prune(cleanup.AllUnusedImages, head) + " 2>&1");
        }

        if (cleanup.UnusedNetworks)
        {
            await Step("Unused networks", head => NetworkCommands.Prune(head) + " 2>&1");
        }

        if (cleanup.BuildCache)
        {
            await Step("Build cache", SystemCommands.BuilderPrune);
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing chosen.");
        }

        return (ok, lines);
    }
}
