using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MatDock.Core.Environments;

/// <summary>
/// Connectivity operations against a remote Docker host over SSH. It runs the Docker CLI on the
/// remote (structured <c>--format '{{json .}}'</c> output) so failures produce clear, actionable
/// messages (Docker missing, no daemon, missing socket permission) instead of an opaque tunnel error.
/// </summary>
public sealed class EnvironmentConnectionService : IEnvironmentConnectionService
{
    private readonly ISshClientFactory _sshClientFactory;
    private readonly MatDockOptions _options;
    private readonly ILogger<EnvironmentConnectionService> _logger;

    public EnvironmentConnectionService(
        ISshClientFactory sshClientFactory,
        IOptions<MatDockOptions> options,
        ILogger<EnvironmentConnectionService> logger)
    {
        _sshClientFactory = sshClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DockerConnectionResult> TestConnectionAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _sshClientFactory.Create(settings);
            await ConnectAsync(client, settings, cancellationToken);

            // Auto-detect how Docker is reachable on this host and return the working access.
            // Keep the FIRST (default) probe's error for the failure message — it is the most
            // representative cause; the later sudo/rootless probes tend to mask it.
            (int ExitStatus, string StdOut, string StdErr) primaryProbe = (-1, string.Empty, string.Empty);
            var isFirstProbe = true;
            foreach (var (useSudo, dockerHost) in BuildCandidates(client, settings))
            {
                var head = VolumeCommands.DockerHead(useSudo, dockerHost);
                var probe = RunCommand(client, VolumeCommands.ServerVersion(head), settings);
                if (probe.ExitStatus == 0 && !string.IsNullOrWhiteSpace(probe.StdOut))
                {
                    var (version, apiVersion, osArch) = ParseServerVersion(probe.StdOut);
                    var label = AccessLabel(useSudo, dockerHost);
                    return DockerConnectionResult.Ok(
                        $"Verbunden mit Docker {version} (API {apiVersion}) · Zugriff: {label}.",
                        version, apiVersion, osArch, useSudo, dockerHost, label);
                }

                if (isFirstProbe)
                {
                    primaryProbe = probe;
                    isFirstProbe = false;
                }
            }

            return DockerConnectionResult.Fail(InterpretDockerError(primaryProbe.StdErr, primaryProbe.StdOut));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DockerConnectionResult.Fail("Zeitüberschreitung beim Verbindungsaufbau.", EnvironmentStatus.Offline);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Connection test to {Host}:{Port} failed.", settings.Host, settings.Port);
            return DockerConnectionResult.Fail(DescribeSshError(ex), EnvironmentStatus.Error);
        }
    }

    public async Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _sshClientFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException)
        {
            // Turn raw SSH errors (e.g. "Permission denied (password)") into an actionable message.
            throw new InvalidOperationException(DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, VolumeCommands.VolumeList(head), settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(InterpretDockerError(result.StdErr, result.StdOut));
        }

        return ParseVolumes(result.StdOut);
    }

    public async Task<HostStats> GetHostStatsAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _sshClientFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, HostStats.BuildCommand(head), settings);
        return HostStats.Parse(result.StdOut);
    }

    /// <summary>Ordered access strategies to probe: default, sudo, then any discovered rootless sockets.</summary>
    private IReadOnlyList<(bool UseSudo, string? DockerHost)> BuildCandidates(SshClient client, SshConnectionSettings settings)
    {
        var candidates = new List<(bool, string?)>
        {
            (false, null),
            (true, null),
        };

        var listed = RunCommand(client, "ls -d /run/user/*/docker.sock 2>/dev/null", settings);
        if (listed.ExitStatus == 0)
        {
            foreach (var line in listed.StdOut.Split('\n'))
            {
                var path = line.Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                var host = $"unix://{path}";
                if (VolumeCommands.IsValidDockerHost(host))
                {
                    candidates.Add((false, host));
                    candidates.Add((true, host));
                }
            }
        }

        return candidates;
    }

    private static string AccessLabel(bool useSudo, string? dockerHost)
    {
        if (dockerHost is not null)
        {
            var path = dockerHost.StartsWith("unix://", StringComparison.Ordinal) ? dockerHost[7..] : dockerHost;
            return useSudo ? $"rootless+sudo ({path})" : $"rootless ({path})";
        }

        return useSudo ? "sudo" : "Standard";
    }

    private async Task ConnectAsync(SshClient client, SshConnectionSettings settings, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
        await client.ConnectAsync(cts.Token);
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(SshClient client, string command, SshConnectionSettings settings)
    {
        using var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds));
        var stdout = sshCommand.Execute();
        return (sshCommand.ExitStatus ?? -1, stdout ?? string.Empty, sshCommand.Error ?? string.Empty);
    }

    private static (string Version, string ApiVersion, string OsArch) ParseServerVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "?";
            var os = Get("Os");
            var arch = Get("Arch");
            return (Get("Version"), Get("ApiVersion"), $"{os}/{arch}");
        }
        catch (JsonException)
        {
            return ("?", "?", "?");
        }
    }

    private static IReadOnlyList<DockerVolume> ParseVolumes(string output)
    {
        var volumes = new List<DockerVolume>();
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
                string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;

                volumes.Add(new DockerVolume
                {
                    Name = Get("Name") ?? string.Empty,
                    Driver = Get("Driver") ?? string.Empty,
                    Mountpoint = Get("Mountpoint"),
                    Scope = Get("Scope"),
                    Labels = ParseLabels(Get("Labels")),
                });
            }
            catch (JsonException)
            {
                // Skip malformed lines rather than failing the whole listing.
            }
        }

        return volumes
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ParseLabels(string? labels)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(labels))
        {
            return result;
        }

        foreach (var pair in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        return result;
    }

    private static string InterpretDockerError(string stderr, string stdout)
    {
        var detail = FirstLine(stderr) ?? FirstLine(stdout) ?? "keine Fehlerausgabe";
        var lower = detail.ToLowerInvariant();

        string? hint = null;
        if (lower.Contains("permission denied") || lower.Contains("got permission denied"))
        {
            hint = "Keine Berechtigung für den Docker-Socket. Der SSH-Benutzer muss den Docker-Daemon erreichen dürfen "
                 + "(Benutzer in Gruppe \"docker\", oder – bei Rootless-Docker – als der Docker-Besitzer verbinden). ";
        }
        else if (lower.Contains("cannot connect to the docker daemon") || lower.Contains("is the docker daemon running")
                 || lower.Contains("connection refused"))
        {
            hint = "Der Docker-Daemon ist nicht erreichbar. Läuft der Docker-Dienst (ggf. Rootless-Socket)? ";
        }
        else if (lower.Contains("not found") || lower.Contains("no such file"))
        {
            hint = "Docker wurde nicht gefunden. Ist Docker installiert und im PATH des SSH-Benutzers? ";
        }

        // Always surface the real remote error so per-host causes are diagnosable.
        return hint is null
            ? $"Docker-Fehler: {detail}"
            : $"{hint}Details: {detail}";
    }

    private static string DescribeSshError(Exception ex) => ex switch
    {
        SshAuthenticationException => "Authentifizierung fehlgeschlagen. Bei Benutzer 'root' ist der Passwort-Login "
            + "oft gesperrt (sshd: PermitRootLogin prohibit-password / PasswordAuthentication no) – dann SSH-Key "
            + "verwenden oder einen Benutzer der Gruppe 'docker'.",
        SshConnectionException => "SSH-Verbindung fehlgeschlagen.",
        System.Net.Sockets.SocketException => "Host nicht erreichbar (Adresse/Port prüfen).",
        _ => $"Fehler: {Innermost(ex).Message}"
    };

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }
}
