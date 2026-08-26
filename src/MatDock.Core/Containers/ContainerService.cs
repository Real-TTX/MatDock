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

        return ParseContainers(result.StdOut);
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
