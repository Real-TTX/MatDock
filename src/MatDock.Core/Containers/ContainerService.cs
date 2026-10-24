using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Containers;

/// <summary>Lists and controls Docker containers on an environment (remote over SSH or the local socket).</summary>
public sealed class ContainerService
{
    private readonly IHostSessionFactory _hostSessionFactory;
    private readonly EnvironmentService _environmentService;
    private readonly MatDockOptions _options;
    private readonly ILogger<ContainerService> _logger;

    public ContainerService(
        IHostSessionFactory hostSessionFactory,
        EnvironmentService environmentService,
        IOptions<MatDockOptions> options,
        ILogger<ContainerService> logger)
    {
        _hostSessionFactory = hostSessionFactory;
        _environmentService = environmentService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DockerContainer>> ListAsync(DockerEnvironment environment, CancellationToken ct = default, bool includeStats = true)
    {
        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var client = _hostSessionFactory.Create(settings);
        await ConnectAsync(client, settings, ct);

        var result = await RunCommandAsync(client, ContainerCommands.List(head), ct);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        var containers = ParseContainers(result.StdOut);

        // `docker stats --no-stream` costs a sampling interval; skip it for callers that only need the
        // container list (e.g. the Stacks overview) to keep the page fast.
        if (!includeStats)
        {
            return containers;
        }

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
            using var client = _hostSessionFactory.Create(settings);
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
            using var client = _hostSessionFactory.Create(settings);
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

    /// <summary>
    /// Recent container lifecycle events (created/started/stopped/…) from the last <paramref name="sinceHours"/>
    /// hours, newest first. Best-effort: returns an empty list if the host is unreachable or its Docker is too
    /// old to support the events window.
    /// </summary>
    public async Task<IReadOnlyList<ContainerEvent>> ListEventsAsync(DockerEnvironment environment, int sinceHours = 24, CancellationToken ct = default)
    {
        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _hostSessionFactory.Create(settings);
            await ConnectAsync(client, settings, ct);
            var result = await RunCommandAsync(client, ContainerCommands.Events(head, sinceHours), ct);
            if (result.ExitStatus != 0)
            {
                return Array.Empty<ContainerEvent>();
            }

            return ParseEvents(result.StdOut)
                .OrderByDescending(e => e.TimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "docker events unavailable for env {Env}.", environment.Id);
            return Array.Empty<ContainerEvent>();
        }
    }

    /// <summary>Service, image reference and image id of every container of a compose project; empty when it cannot be read.</summary>
    public async Task<IReadOnlyList<(string Service, string Reference, string ImageId)>> ImagesOfProjectAsync(DockerEnvironment environment, string project, CancellationToken ct = default)
    {
        var containers = await ListByProjectAsync(environment, project, ct);
        if (containers.Count == 0)
        {
            return Array.Empty<(string, string, string)>();
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        try
        {
            using var client = _hostSessionFactory.Create(settings);
            await ConnectAsync(client, settings, ct);
            var result = await RunCommandAsync(client, ContainerCommands.ImagesOf(head, containers.Select(c => c.Id).ToList()), ct);
            return result.ExitStatus == 0 ? ContainerCommands.ParseImagesOf(result.StdOut) : Array.Empty<(string, string, string)>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Images of {Project} on env {Env} not read.", project, environment.Id);
            return Array.Empty<(string, string, string)>();
        }
    }

    /// <summary>Runs one docker command on the environment; (exit code 0, output).</summary>
    /// <param name="timeout">How long the command may take; the short SSH timeout by default (a pull needs <see cref="LongTimeout"/>).</param>
    public async Task<(bool Ok, string Output)> RunAsync(DockerEnvironment environment, Func<string, string> command, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        using var client = _hostSessionFactory.Create(settings);
        await ConnectAsync(client, settings, ct);
        var result = await RunCommandAsync(client, command(head), ct, timeout);
        return (result.ExitStatus == 0, (result.StdOut + result.StdErr).Trim());
    }

    /// <summary>The alerts (died with an error, unhealthy) between two Unix times; null when the host cannot be asked.</summary>
    public async Task<IReadOnlyList<ContainerAlert>?> ListAlertsAsync(DockerEnvironment environment, long sinceUnix, long untilUnix, CancellationToken ct = default)
    {
        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _hostSessionFactory.Create(settings);
            await ConnectAsync(client, settings, ct);
            var result = await RunCommandAsync(client, ContainerAlerts.EventsCommand(head, sinceUnix, untilUnix), ct);
            return result.ExitStatus == 0 ? ContainerAlerts.Detect(result.StdOut, environment.Name) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "docker events for alerts unavailable for env {Env}.", environment.Id);
            return null;
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

        using var client = _hostSessionFactory.Create(settings);
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

    /// <summary>The mounts (named volumes/binds) of one container. Best-effort: empty on failure. One inspect call.</summary>
    public async Task<IReadOnlyList<ContainerMount>> GetMountsAsync(DockerEnvironment environment, string id, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return Array.Empty<ContainerMount>();
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _hostSessionFactory.Create(settings);
            await ConnectAsync(client, settings, ct);
            var inspect = await RunCommandAsync(client, ContainerCommands.InspectMounts(head, id), ct);
            return inspect.ExitStatus == 0 ? ParseMounts(inspect.StdOut) : Array.Empty<ContainerMount>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetMounts for {Id} on env {Env} failed.", id, environment.Id);
            return Array.Empty<ContainerMount>();
        }
    }

    public async Task<(bool Success, string Message)> ActionAsync(DockerEnvironment environment, string id, ContainerAction action, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return (false, "Invalid container ID.");
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _hostSessionFactory.Create(settings);
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

    /// <summary>Everything <c>docker inspect</c> says about one container, secrets masked; null when it is not there.</summary>
    public async Task<ContainerInspect?> InspectAsync(DockerEnvironment environment, string id, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return null;
        }

        var (ok, output) = await RunAsync(environment, head => ContainerCommands.Inspect(head, id), ct);
        return ok ? ContainerInspect.Parse(output) : null;
    }

    /// <summary>The processes of a running container; an empty table when it is not running or <c>docker top</c> refuses.</summary>
    public async Task<(ContainerProcessTable Table, string? Error)> TopAsync(DockerEnvironment environment, string id, CancellationToken ct = default)
    {
        var empty = new ContainerProcessTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        if (!ContainerCommands.IsValidId(id))
        {
            return (empty, "Invalid container ID.");
        }

        var (ok, output) = await RunAsync(environment, head => ContainerCommands.Top(head, id), ct);
        return ok ? (ContainerInspect.ParseTop(output), null) : (empty, DockerErrorMessages.InterpretDockerError(output, output));
    }

    /// <summary>Removes a container (stopped, or running with <paramref name="force"/>); with <paramref name="volumes"/> also its anonymous volumes.</summary>
    public async Task<(bool Success, string Message)> RemoveAsync(DockerEnvironment environment, string id, bool force, bool volumes, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return (false, "Invalid container ID.");
        }

        try
        {
            var (ok, output) = await RunAsync(environment, head => ContainerCommands.Remove(head, id, force, volumes), ct);
            return ok ? (true, "Removed.") : (false, DockerErrorMessages.InterpretDockerError(output, output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Removing container {Id} failed.", id);
            return (false, ex.Message);
        }
    }

    /// <summary>Gives a container another name.</summary>
    public async Task<(bool Success, string Message)> RenameAsync(DockerEnvironment environment, string id, string newName, CancellationToken ct = default)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (!ContainerCommands.IsValidId(id))
        {
            return (false, "Invalid container ID.");
        }

        if (!ContainerCommands.IsValidId(newName))
        {
            return (false, "A name starts with a letter or digit and has only letters, digits, '_', '.' and '-' (at most 128).");
        }

        try
        {
            var (ok, output) = await RunAsync(environment, head => ContainerCommands.Rename(head, id, newName), ct);
            return ok ? (true, $"Renamed to {newName}.") : (false, DockerErrorMessages.InterpretDockerError(output, output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Renaming container {Id} failed.", id);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// The settings that start this container again (see <see cref="ContainerRunSpecs.FromInspect"/>), with the real values of its environment: for MatDock's
    /// own use only (recreate, the secrets of a duplicate); never shown on a page. Null when the container is not there.
    /// </summary>
    public async Task<(ContainerRunSpec Spec, ContainerInspect Container)?> GetRunSpecAsync(DockerEnvironment environment, string id, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return null;
        }

        var (ok, output) = await RunAsync(environment, head => ContainerCommands.Inspect(head, id), ct);
        if (!ok || ContainerInspect.Parse(output, maskSecrets: false) is not { } container)
        {
            return null;
        }

        var image = ImageConfig.Empty;
        if (container.ImageId is { } imageId && Images.ImageCommands.IsValidImageRef(imageId))
        {
            var (imageOk, imageJson) = await RunAsync(environment,
                head => VolumeCommands.PathPrefix + head + " image inspect --format '{{json .Config}}' " + VolumeFileCommands.ShellQuote(imageId), ct);
            image = imageOk ? ImageConfig.Parse(imageJson) : ImageConfig.Empty;
        }

        return (ContainerRunSpecs.FromInspect(container, image), container);
    }

    /// <summary>Changes the limits or the restart policy of a container (<c>docker update</c>).</summary>
    public async Task<(bool Success, string Message)> UpdateAsync(DockerEnvironment environment, string id, ContainerUpdateSpec update, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return (false, "Invalid container ID.");
        }

        if (ContainerRunCommands.Validate(update) is { } invalid)
        {
            return (false, invalid);
        }

        try
        {
            var (ok, output) = await RunAsync(environment, head => ContainerRunCommands.Update(head, id, update), ct);
            return ok ? (true, "Updated.") : (false, DockerErrorMessages.InterpretDockerError(output, output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "docker update of {Id} failed.", id);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Recreates a container with the same settings (and with <paramref name="pull"/> the newest image of its tag): the old one is renamed and stopped, the new one
    /// takes its name, and the old one is removed once the new one runs. When the new one does not start, the old one gets its name back and runs again.
    /// Anonymous volumes are handed over by name, so their data stays. Not for a container of a stack: its stack recreates it.
    /// </summary>
    public async Task<(bool Success, string Message)> RecreateAsync(DockerEnvironment environment, string id, bool pull, Action<string>? log = null, CancellationToken ct = default)
    {
        var read = await GetRunSpecAsync(environment, id, ct);
        if (read is not { } found)
        {
            return (false, "Container not found.");
        }

        var (spec, old) = found;
        if (old.Project is not null)
        {
            return (false, $"This container belongs to the stack '{old.Project}': recreate it with the stack.");
        }

        if (ContainerRunCommands.Validate(spec) is { } invalid)
        {
            return (false, $"Its settings cannot be repeated by MatDock: {invalid}");
        }

        string Q(string s) => VolumeFileCommands.ShellQuote(s);
        var oldName = old.Name;
        var parked = $"{oldName}-old-{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (!ContainerCommands.IsValidId(parked))
        {
            parked = $"matdock-old-{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        if (pull)
        {
            log?.Invoke($"==> docker pull {spec.Image}");
            var (pulled, pullOutput) = await RunAsync(environment, head => VolumeCommands.PathPrefix + head + " pull " + Q(spec.Image) + " 2>&1", ct, LongTimeout);
            if (!pulled)
            {
                return (false, $"Pull failed, nothing changed: {DockerErrorMessages.InterpretDockerError(pullOutput, pullOutput)}");
            }
        }

        log?.Invoke($"==> {oldName} → {parked}, stop");
        var (renamed, renameOutput) = await RunAsync(environment, head => ContainerCommands.Rename(head, old.Id, parked), ct);
        if (!renamed)
        {
            return (false, $"Could not set the old container aside: {DockerErrorMessages.InterpretDockerError(renameOutput, renameOutput)}");
        }

        if (old.Running)
        {
            await RunAsync(environment, head => ContainerCommands.Action(head, ContainerAction.Stop, old.Id), ct);
        }

        log?.Invoke("==> docker run (the same settings)");
        spec.Name = oldName;
        var (started, runOutput) = await RunAsync(environment, head => ContainerRunCommands.Run(spec, head), ct, LongTimeout);
        if (!started)
        {
            // Back to how it was: a half-created new one goes, the old one gets its name back and runs again.
            await RunAsync(environment, head => ContainerCommands.Remove(head, oldName, force: true, volumes: false), ct);
            await RunAsync(environment, head => ContainerCommands.Rename(head, old.Id, oldName), ct);
            if (old.Running)
            {
                await RunAsync(environment, head => ContainerCommands.Action(head, ContainerAction.Start, old.Id), ct);
            }

            return (false, $"The new container did not start; the old one runs again: {DockerErrorMessages.InterpretDockerError(runOutput, runOutput)}");
        }

        log?.Invoke($"==> remove {parked}");
        // Without -v: its anonymous volumes are the new container's now.
        var (removed, removeOutput) = await RunAsync(environment, head => ContainerCommands.Remove(head, old.Id, force: true, volumes: false), ct);
        return removed
            ? (true, $"Recreated {oldName}.")
            : (true, $"Recreated {oldName}; the old container '{parked}' could not be removed: {DockerErrorMessages.InterpretDockerError(removeOutput, removeOutput)}");
    }

    /// <summary>Removes all stopped containers of an environment; the message says how much space it freed.</summary>
    public async Task<(bool Success, string Message)> PruneStoppedAsync(DockerEnvironment environment, CancellationToken ct = default)
    {
        try
        {
            var (ok, output) = await RunAsync(environment, ContainerCommands.PruneStopped, ct);
            if (!ok)
            {
                return (false, DockerErrorMessages.InterpretDockerError(output, output));
            }

            var removed = output.Split('\n').Count(l => l.Trim().Length == 64 && l.Trim().All(char.IsAsciiHexDigitLower));
            var reclaimed = output.Split('\n').FirstOrDefault(l => l.StartsWith("Total reclaimed space", StringComparison.OrdinalIgnoreCase))?.Trim();
            return (true, $"Removed {removed} stopped container(s).{(reclaimed is null ? string.Empty : " " + reclaimed + ".")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Container prune on env {Env} failed.", environment.Id);
            return (false, ex.Message);
        }
    }

    /// <summary>Starts an ad-hoc container (<c>docker run -d</c>). Returns the new container id or an actionable message.</summary>
    public async Task<(bool Success, string Message)> RunAsync(DockerEnvironment environment, ContainerRunSpec spec, CancellationToken ct = default)
    {
        var invalid = ContainerRunCommands.Validate(spec);
        if (invalid is not null)
        {
            return (false, invalid);
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        try
        {
            using var client = _hostSessionFactory.Create(settings);
            await ConnectAsync(client, settings, ct);

            // docker run pulls an image it does not have: that takes longer than a quick command.
            var result = await RunCommandAsync(client, ContainerRunCommands.Run(spec, head), ct, LongTimeout);
            var output = (result.StdOut ?? string.Empty).Trim();
            // 2>&1 merges the pull progress into stdout; the container id is the last line.
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
            return result.ExitStatus == 0
                ? (true, last.Length >= 12 ? $"Started {last[..12]}." : "Started.")
                : (false, DockerErrorMessages.InterpretDockerError(result.StdErr, output));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Container run of {Image} failed.", spec.Image);
            return (false, ex.Message);
        }
    }

    public async Task<string> LogsAsync(DockerEnvironment environment, string id, int tail = 200, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(id))
        {
            return "Invalid container ID.";
        }

        var settings = _environmentService.BuildSettings(environment);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var client = _hostSessionFactory.Create(settings);
        await ConnectAsync(client, settings, ct);

        // stderr is merged into stdout (2>&1) because containers legitimately log to stderr, so the
        // exit status is the only reliable failure signal — a non-zero exit means the merged output
        // is a docker error (e.g. "No such container"), not real log content.
        var result = await RunCommandAsync(client, ContainerCommands.Logs(head, id, tail), ct);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdOut, result.StdOut));
        }

        return string.IsNullOrWhiteSpace(result.StdOut) ? "(no log output)" : result.StdOut;
    }

    private async Task ConnectAsync(IHostSession client, SshConnectionSettings settings, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
        try
        {
            await client.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Connection timed out.");
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            // Turn raw SSH errors (e.g. "Permission denied (password)") into an actionable message.
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }
    }

    /// <summary>The time a command that pulls an image may take.</summary>
    private TimeSpan LongTimeout => TimeSpan.FromSeconds(Math.Max(60, _options.MigrationTimeoutSeconds));

    private async Task<(int ExitStatus, string StdOut, string StdErr)> RunCommandAsync(IHostSession client, string command, CancellationToken ct, TimeSpan? limit = null)
    {
        using var cmd = client.CreateCommand(command);
        var timeout = limit ?? TimeSpan.FromSeconds(Math.Max(10, _options.SshTimeoutSeconds));
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
                    OpenUrlOverride = ExtractLabel(labels, "matdock.openurl"),
                    Networks = (Get("Networks") ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
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

    /// <summary>Parses the JSON-lines output of <c>docker events --format '{{json .}}'</c> into events.</summary>
    public static IReadOnlyList<ContainerEvent> ParseEvents(string output)
    {
        var list = new List<ContainerEvent>();
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

                static string? Str(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                // Defensive: the command already filters to container, but a mixed daemon could still slip.
                var type = Str(root, "Type") ?? "container";
                if (!string.Equals(type, "container", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Newer daemons emit "Action"; legacy ones "status". Actions may carry a suffix
                // ("exec_create: sh") — keep only the head token, lower-cased.
                var action = Str(root, "Action") ?? Str(root, "status") ?? string.Empty;
                var colon = action.IndexOf(':');
                if (colon > 0)
                {
                    action = action[..colon];
                }

                action = action.Trim().ToLowerInvariant();
                if (action.Length == 0)
                {
                    continue;
                }

                string? name = null, image = null;
                var id = Str(root, "id");
                if (root.TryGetProperty("Actor", out var actor) && actor.ValueKind == JsonValueKind.Object)
                {
                    id = Str(actor, "ID") ?? id;
                    if (actor.TryGetProperty("Attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                    {
                        name = Str(attrs, "name");
                        image = Str(attrs, "image");
                    }
                }

                image ??= Str(root, "from"); // legacy: image is reported as "from"
                var shortId = string.IsNullOrEmpty(id) ? string.Empty : ShortId(id);

                long unix = 0;
                if (root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    unix = t.GetInt64();
                }

                var timeUtc = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : default;

                list.Add(new ContainerEvent(
                    timeUtc,
                    action,
                    name ?? (shortId.Length > 0 ? shortId : "?"),
                    image,
                    shortId));
            }
            catch (JsonException)
            {
                // skip malformed line
            }
        }

        return list;
    }

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
