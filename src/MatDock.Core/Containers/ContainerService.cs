using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MatDock.Core.Containers;

/// <summary>Lists and controls Docker containers on a remote environment over SSH.</summary>
public sealed class ContainerService
{
    private readonly ISshClientFactory _sshClientFactory;
    private readonly EnvironmentService _environmentService;
    private readonly MatDockOptions _options;
    private readonly ILogger<ContainerService> _logger;

    public ContainerService(
        ISshClientFactory sshClientFactory,
        EnvironmentService environmentService,
        IOptions<MatDockOptions> options,
        ILogger<ContainerService> logger)
    {
        _sshClientFactory = sshClientFactory;
        _environmentService = environmentService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DockerContainer>> ListAsync(DockerEnvironment environment, CancellationToken ct = default)
    {
        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var client = _sshClientFactory.Create(settings);
        await ConnectAsync(client, settings, ct);

        var result = await RunCommandAsync(client, ContainerCommands.List(head), ct);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        var containers = ParseContainers(result.StdOut);

        // Best-effort live usage on the same connection; never fail the listing if stats are unavailable.
        try
        {
            var statsResult = await RunCommandAsync(client, ContainerCommands.Stats(head), ct);
            if (statsResult.ExitStatus == 0)
            {
                ApplyStats(containers, statsResult.StdOut);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "docker stats unavailable for env {Env}.", environment.Id);
        }

        return containers;
    }

    /// <summary>Containers that mount the given volume on this host (running and stopped). Empty on failure.</summary>
    public async Task<IReadOnlyList<DockerContainer>> ListByVolumeAsync(DockerEnvironment environment, string volume, CancellationToken ct = default)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return Array.Empty<DockerContainer>();
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _sshClientFactory.Create(settings);
            await ConnectAsync(client, settings, ct);
            var result = await RunCommandAsync(client, ContainerCommands.ListByVolume(head, volume, runningOnly: false), ct);
            return result.ExitStatus == 0 ? ParseContainers(result.StdOut) : Array.Empty<DockerContainer>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ListByVolume for {Volume} on env {Env} failed.", volume, environment.Id);
            return Array.Empty<DockerContainer>();
        }
    }

    /// <summary>All containers of a compose project (stack), with live stats merged. Empty on failure.</summary>
    public async Task<IReadOnlyList<DockerContainer>> ListByProjectAsync(DockerEnvironment environment, string project, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(project))
        {
            return Array.Empty<DockerContainer>();
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _sshClientFactory.Create(settings);
            await ConnectAsync(client, settings, ct);
            var result = await RunCommandAsync(client, ContainerCommands.ListByProject(head, project), ct);
            if (result.ExitStatus != 0)
            {
                return Array.Empty<DockerContainer>();
            }

            var containers = ParseContainers(result.StdOut);
            try
            {
                var stats = await RunCommandAsync(client, ContainerCommands.Stats(head), ct);
                if (stats.ExitStatus == 0)
                {
                    ApplyStats(containers, stats.StdOut);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "docker stats unavailable for env {Env}.", environment.Id);
            }

            return containers;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ListByProject {Project} on env {Env} failed.", project, environment.Id);
            return Array.Empty<DockerContainer>();
        }
    }

    /// <summary>One container by id plus its mounts and live stats. Container is null if not found / unreachable.</summary>
    public async Task<(DockerContainer? Container, IReadOnlyList<ContainerMount> Mounts)> GetDetailAsync(DockerEnvironment environment, string id, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return (null, Array.Empty<ContainerMount>());
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var client = _sshClientFactory.Create(settings);
        await ConnectAsync(client, settings, ct);

        var psResult = await RunCommandAsync(client, ContainerCommands.Get(head, id), ct);
        var container = psResult.ExitStatus == 0 ? ParseContainers(psResult.StdOut).FirstOrDefault() : null;
        if (container is null)
        {
            return (null, Array.Empty<ContainerMount>());
        }

        try
        {
            var stats = await RunCommandAsync(client, ContainerCommands.Stats(head), ct);
            if (stats.ExitStatus == 0)
            {
                ApplyStats(new[] { container }, stats.StdOut);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "docker stats unavailable for env {Env}.", environment.Id);
        }

        IReadOnlyList<ContainerMount> mounts = Array.Empty<ContainerMount>();
        try
        {
            var inspect = await RunCommandAsync(client, ContainerCommands.InspectMounts(head, container.Id.Length > 0 ? container.Id : id), ct);
            if (inspect.ExitStatus == 0)
            {
                mounts = ParseMounts(inspect.StdOut);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "inspect mounts unavailable for {Id}.", id);
        }

        return (container, mounts);
    }

    public async Task<(bool Success, string Message)> ActionAsync(DockerEnvironment environment, string id, ContainerAction action, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return (false, "Ungültige Container-ID.");
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _sshClientFactory.Create(settings);
            await ConnectAsync(client, settings, ct);

            var result = await RunCommandAsync(client, ContainerCommands.Action(head, action, id), ct);
            return result.ExitStatus == 0
                ? (true, $"{action}: OK")
                : (false, DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Container {Action} on {Id} failed.", action, id);
            return (false, ex.Message);
        }
    }

    public async Task<string> LogsAsync(DockerEnvironment environment, string id, int tail = 200, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return "Ungültige Container-ID.";
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var client = _sshClientFactory.Create(settings);
        await ConnectAsync(client, settings, ct);

        // stderr is merged into stdout (2>&1) because containers legitimately log to stderr, so the
        // exit status is the only reliable failure signal — a non-zero exit means the merged output
        // is a docker error (e.g. "No such container"), not real log content.
        var result = await RunCommandAsync(client, ContainerCommands.Logs(head, id, tail), ct);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdOut, result.StdOut));
        }

        return string.IsNullOrWhiteSpace(result.StdOut) ? "(keine Logausgabe)" : result.StdOut;
    }

    private async Task ConnectAsync(SshClient client, SshConnectionSettings settings, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
        try
        {
            await client.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Zeitüberschreitung beim Verbindungsaufbau.");
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            // Turn raw SSH errors (e.g. "Permission denied (password)") into an actionable message.
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }
    }

    private async Task<(int ExitStatus, string StdOut, string StdErr)> RunCommandAsync(SshClient client, string command, CancellationToken ct)
    {
        using var cmd = client.CreateCommand(command);
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.SshTimeoutSeconds));
        cmd.CommandTimeout = timeout;

        // Honor request cancellation/shutdown and still bound the command by the configured timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await cmd.ExecuteAsync(cts.Token);
        return (cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
    }

    public static IReadOnlyList<DockerContainer> ParseContainers(string output)
    {
        var list = new List<DockerContainer>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    // Valid JSON but not an object (stray number/array/null line) — skip it.
                    continue;
                }

                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

                var labels = Get("Labels") ?? string.Empty;
                list.Add(new DockerContainer
                {
                    Id = Get("ID") ?? string.Empty,
                    Name = Get("Names") ?? string.Empty,
                    Image = Get("Image") ?? string.Empty,
                    State = Get("State") ?? string.Empty,
                    Status = Get("Status") ?? string.Empty,
                    Ports = Get("Ports"),
                    Project = ExtractLabel(labels, "com.docker.compose.project"),
                    Service = ExtractLabel(labels, "com.docker.compose.service"),
                });
            }
            catch (JsonException)
            {
                // skip malformed line
            }
        }

        return list;
    }

    /// <summary>Merges <c>docker stats --format json</c> output into the matching containers (by id).</summary>
    public static void ApplyStats(IReadOnlyList<DockerContainer> containers, string statsOutput)
        => MergeStats(containers, ParseStats(statsOutput));

    /// <summary>Parses the JSON array from <c>docker inspect --format '{{json .Mounts}}'</c>.</summary>
    public static IReadOnlyList<ContainerMount> ParseMounts(string json)
    {
        var line = json.Trim();
        if (line.Length == 0)
        {
            return Array.Empty<ContainerMount>();
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ContainerMount>();
            }

            var mounts = new List<ContainerMount>();
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? Get(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var rw = !m.TryGetProperty("RW", out var rwv) || rwv.ValueKind != JsonValueKind.False;
                mounts.Add(new ContainerMount(
                    Get("Type") ?? string.Empty,
                    Get("Name"),
                    Get("Source"),
                    Get("Destination") ?? string.Empty,
                    rw));
            }

            return mounts;
        }
        catch (JsonException)
        {
            return Array.Empty<ContainerMount>();
        }
    }

    private sealed record StatsEntry(double? Cpu, double? Mem, string? Usage);

    private static Dictionary<string, StatsEntry> ParseStats(string output)
    {
        var map = new Dictionary<string, StatsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

                var id = Get("ID");
                if (!string.IsNullOrEmpty(id))
                {
                    // `docker stats` may report the full 64-char id while `docker ps` reports the 12-char
                    // short id — normalize both to the short form so the merge matches across versions.
                    map[ShortId(id)] = new StatsEntry(ParsePercent(Get("CPUPerc")), ParsePercent(Get("MemPerc")), Get("MemUsage"));
                }
            }
            catch (JsonException)
            {
                // skip malformed line
            }
        }

        return map;
    }

    private static void MergeStats(IReadOnlyList<DockerContainer> containers, Dictionary<string, StatsEntry> stats)
    {
        foreach (var c in containers)
        {
            if (c.Id.Length > 0 && stats.TryGetValue(ShortId(c.Id), out var s))
            {
                c.CpuPercent = s.Cpu;
                c.MemPercent = s.Mem;
                c.MemUsage = s.Usage;
            }
        }
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    private static double? ParsePercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().TrimEnd('%').Trim();
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private static string? ExtractLabel(string labels, string key)
    {
        foreach (var pair in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
            {
                return pair[(eq + 1)..];
            }
        }

        return null;
    }
}
