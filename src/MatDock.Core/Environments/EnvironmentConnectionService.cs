using Docker.DotNet.Models;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Environments;

/// <inheritdoc />
public sealed class EnvironmentConnectionService : IEnvironmentConnectionService
{
    private readonly IDockerHostFactory _hostFactory;
    private readonly MatDockOptions _options;
    private readonly ILogger<EnvironmentConnectionService> _logger;

    public EnvironmentConnectionService(
        IDockerHostFactory hostFactory,
        IOptions<MatDockOptions> options,
        ILogger<EnvironmentConnectionService> logger)
    {
        _hostFactory = hostFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DockerConnectionResult> TestConnectionAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeoutScope(cancellationToken);
        try
        {
            await using var host = await _hostFactory.CreateAsync(settings, timeout.Token);
            await host.Client.System.PingAsync(timeout.Token);
            var version = await host.Client.System.GetVersionAsync(timeout.Token);

            var message = $"Verbunden mit Docker {version.Version} (API {version.APIVersion}).";
            return DockerConnectionResult.Ok(message, version.Version, version.APIVersion, $"{version.Os}/{version.Arch}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DockerConnectionResult.Fail("Zeitüberschreitung beim Verbindungsaufbau.", EnvironmentStatus.Offline);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Connection test to {Host}:{Port} failed.", settings.Host, settings.Port);
            return DockerConnectionResult.Fail(Describe(ex), EnvironmentStatus.Error);
        }
    }

    public async Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeoutScope(cancellationToken);
        await using var host = await _hostFactory.CreateAsync(settings, timeout.Token);

        var response = await host.Client.Volumes.ListAsync(timeout.Token);
        var volumes = response.Volumes ?? new List<VolumeResponse>();

        return volumes
            .Select(v => new DockerVolume
            {
                Name = v.Name ?? string.Empty,
                Driver = v.Driver ?? string.Empty,
                Mountpoint = v.Mountpoint,
                Scope = v.Scope,
                CreatedAt = v.CreatedAt,
                SizeBytes = v.UsageData is { Size: >= 0 } usage ? usage.Size : null,
                Labels = v.Labels is { Count: > 0 }
                    ? new Dictionary<string, string>(v.Labels)
                    : new Dictionary<string, string>()
            })
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CancellationTokenSource CreateTimeoutScope(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds)));
        return cts;
    }

    private static string Describe(Exception ex) => ex switch
    {
        Renci.SshNet.Common.SshAuthenticationException => "Authentifizierung fehlgeschlagen (Benutzer, Passwort oder Schlüssel prüfen).",
        Renci.SshNet.Common.SshConnectionException => "SSH-Verbindung fehlgeschlagen.",
        System.Net.Sockets.SocketException => "Host nicht erreichbar (Adresse/Port prüfen).",
        global::Docker.DotNet.DockerApiException dae => $"Docker-API-Fehler: {dae.Message}",
        _ => $"Fehler: {ex.Message}"
    };
}
