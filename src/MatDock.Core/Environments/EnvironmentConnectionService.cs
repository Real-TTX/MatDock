using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Ssh;
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
    // Non-interactive SSH sessions often have a minimal PATH; make sure the usual Docker locations
    // are searched so "works in my terminal but not here" does not happen.
    private const string PathPrefix =
        "export PATH=\"$PATH:/usr/local/bin:/usr/bin:/bin:/snap/bin:/usr/sbin:/sbin\"; ";

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

            var probe = RunCommand(client, "docker version --format '{{json .Server}}'", settings);
            if (probe.ExitStatus != 0 || string.IsNullOrWhiteSpace(probe.StdOut))
            {
                return DockerConnectionResult.Fail(InterpretDockerError(probe.StdErr, probe.StdOut));
            }

            var (version, apiVersion, osArch) = ParseServerVersion(probe.StdOut);
            return DockerConnectionResult.Ok(
                $"Verbunden mit Docker {version} (API {apiVersion}).", version, apiVersion, osArch);
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
        await ConnectAsync(client, settings, cancellationToken);

        var result = RunCommand(client, "docker volume ls --format '{{json .}}'", settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(InterpretDockerError(result.StdErr, result.StdOut));
        }

        return ParseVolumes(result.StdOut);
    }

    private async Task ConnectAsync(SshClient client, SshConnectionSettings settings, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
        await client.ConnectAsync(cts.Token);
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(SshClient client, string dockerCommand, SshConnectionSettings settings)
    {
        using var command = client.CreateCommand(PathPrefix + dockerCommand);
        command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds));
        var stdout = command.Execute();
        return (command.ExitStatus ?? -1, stdout ?? string.Empty, command.Error ?? string.Empty);
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
        var message = FirstLine(stderr) ?? FirstLine(stdout) ?? "Unbekannter Fehler bei der Docker-Prüfung.";
        var lower = message.ToLowerInvariant();

        if (lower.Contains("permission denied") || lower.Contains("dial unix") || lower.Contains("got permission denied"))
        {
            return "Keine Berechtigung für den Docker-Daemon. Den SSH-Benutzer zur Gruppe \"docker\" hinzufügen (oder sudo einrichten).";
        }

        if (lower.Contains("not found") || lower.Contains("no such file"))
        {
            return "Docker wurde auf dem Host nicht gefunden. Ist Docker installiert und im PATH des SSH-Benutzers?";
        }

        if (lower.Contains("cannot connect to the docker daemon") || lower.Contains("is the docker daemon running"))
        {
            return "Der Docker-Daemon ist nicht erreichbar. Läuft der Docker-Dienst auf dem Host?";
        }

        return $"Docker-Prüfung fehlgeschlagen: {message}";
    }

    private static string DescribeSshError(Exception ex) => ex switch
    {
        SshAuthenticationException => "Authentifizierung fehlgeschlagen (Benutzer, Passwort oder Schlüssel prüfen).",
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
