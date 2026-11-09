using System.Collections.Concurrent;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Updates;

/// <summary>The last host check of each environment, so the Updates page and the dashboard show it without asking the hosts on every visit. In memory, a restart forgets it.</summary>
public sealed class HostUpdateCache
{
    private readonly ConcurrentDictionary<long, (HostUpdateReport Report, DateTime CheckedUtc)> _reports = new();

    public void Set(long environmentId, HostUpdateReport report) => _reports[environmentId] = (report, DateTime.UtcNow);

    public (HostUpdateReport Report, DateTime CheckedUtc)? Get(long environmentId)
        => _reports.TryGetValue(environmentId, out var entry) ? entry : null;

    private readonly ConcurrentDictionary<long, string> _announced = new();

    /// <summary>
    /// Whether the report is worth a notification: it brings a Docker update or security fixes, and that differs from what was last announced
    /// for the environment (so the same pending updates are announced once, not after every check).
    /// </summary>
    public bool ShouldAnnounce(long environmentId, HostUpdateReport report)
    {
        if (report.Error is not null)
        {
            return false;
        }

        if (report.DockerTarget is null && report.SecurityCount == 0)
        {
            _announced.TryRemove(environmentId, out _);
            return false;
        }

        var signature = $"{report.DockerTarget}|{report.SecurityCount > 0}";
        if (_announced.TryGetValue(environmentId, out var last) && last == signature)
        {
            return false;
        }

        _announced[environmentId] = signature;
        return true;
    }
}

/// <summary>
/// Host updates of an environment: what apt would upgrade on its host (Docker Engine first of all) and upgrading Docker's packages.
/// Everything runs through the environment's own Docker in a throwaway container (see <see cref="HostUpdateCommands"/>), so a Local
/// environment works like an SSH one and no sudo password is needed.
/// </summary>
public sealed class HostUpdateService
{
    /// <summary>Refreshing the package lists downloads from the mirrors; give it far longer than an ordinary command.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(5);

    private readonly IHostSessionFactory _sessions;
    private readonly EnvironmentService _environments;
    private readonly HostUpdateCache _cache;
    private readonly MatDockOptions _options;
    private readonly ILogger<HostUpdateService> _logger;

    public HostUpdateService(IHostSessionFactory sessions, EnvironmentService environments, HostUpdateCache cache,
        IOptions<MatDockOptions> options, ILogger<HostUpdateService> logger)
    {
        _sessions = sessions;
        _environments = environments;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Whether the host of the environment can be looked at: another MatDock (bridge) answers for its own hosts.</summary>
    public static bool Supports(DockerEnvironment env) => env.ConnectionType != ConnectionType.Bridge;

    /// <summary>Asks the host what apt would upgrade (refreshing its package lists first unless <paramref name="refresh"/> is false) and remembers the answer.</summary>
    public async Task<HostUpdateReport> CheckAsync(DockerEnvironment env, bool refresh = true, CancellationToken ct = default)
    {
        if (!Supports(env))
        {
            return HostUpdateReport.Failed("Another MatDock (bridge) looks after the updates of its own hosts.");
        }

        HostUpdateReport report;
        try
        {
            var (_, stdout, stderr) = await RunAsync(env, head => HostUpdateCommands.Check(_options.HelperImage, head, refresh), CheckTimeout, ct);
            report = HostUpdateReport.Parse(stdout, stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Host update check on {Environment} failed", env.Name);
            report = HostUpdateReport.Failed(ex.Message);
        }

        _cache.Set(env.Id, report);
        return report;
    }

    /// <summary>The packages of Docker that the last check found, ready for <see cref="StartUpgradeAsync"/>.</summary>
    public IReadOnlyList<string> PendingDockerPackages(long environmentId)
        => _cache.Get(environmentId)?.Report.DockerPackages.Select(p => p.Name).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>Starts the upgrade of <paramref name="packages"/> on the host; returns the name of its systemd unit for <see cref="ReadUpgradeAsync"/>.</summary>
    public async Task<string> StartUpgradeAsync(DockerEnvironment env, IReadOnlyCollection<string> packages, CancellationToken ct = default)
    {
        var unit = HostUpdateCommands.NewUnitName();
        var (exit, stdout, stderr) = await RunAsync(env, head => HostUpdateCommands.StartUpgrade(packages, unit, _options.HelperImage, head),
            TimeSpan.FromMinutes(2), ct);
        if (exit != 0)
        {
            var said = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"The upgrade could not be started on {env.Name}: {said.Trim()}");
        }

        return unit;
    }

    /// <summary>How far the upgrade is: its exit code once it ended, and the end of its log.</summary>
    public async Task<(int? ExitCode, string Log)> ReadUpgradeAsync(DockerEnvironment env, string unit, CancellationToken ct = default)
    {
        var (_, stdout, _) = await RunAsync(env, head => HostUpdateCommands.ReadUpgrade(unit, _options.HelperImage, head), TimeSpan.FromMinutes(1), ct);
        return HostUpdateCommands.ParseUpgrade(stdout);
    }

    private async Task<(int Exit, string StdOut, string StdErr)> RunAsync(DockerEnvironment env, Func<string, string> build, TimeSpan timeout, CancellationToken ct)
    {
        var settings = _environments.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var command = build(head);

        using var client = _sessions.Create(settings);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
            await client.ConnectAsync(connect.Token);
        }

        return await Task.Run(() =>
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout;
            var stdout = cmd.Execute();
            return (cmd.ExitStatus ?? -1, stdout ?? string.Empty, cmd.Error ?? string.Empty);
        }, ct);
    }
}
