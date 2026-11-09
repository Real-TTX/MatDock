using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Updates;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Looks at the hosts of all environments every six hours (<see cref="HostUpdateService"/>): refreshes their package lists, so the
/// Updates page and the dashboard know about new Docker versions and security fixes without anyone asking, and sends one notification
/// when an environment gets a new Docker version or security fixes. A few minutes after the start, after the environments' first health check.
/// </summary>
public sealed class HostUpdateMonitor : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HostUpdateMonitor> _logger;

    public HostUpdateMonitor(IServiceScopeFactory scopeFactory, ILogger<HostUpdateMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await PassAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Host update pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One look at every host; returns the lines it announced.</summary>
    public static async Task<IReadOnlyList<string>> PassAsync(IServiceProvider services, CancellationToken ct)
    {
        var environments = services.GetRequiredService<EnvironmentService>();
        var hosts = services.GetRequiredService<HostUpdateService>();
        var cache = services.GetRequiredService<HostUpdateCache>();

        var announce = new List<string>();
        foreach (var env in (await environments.GetEnabledAsync(ct)).Where(e => e.Status != EnvironmentStatus.Offline && HostUpdateService.Supports(e)))
        {
            ct.ThrowIfCancellationRequested();
            var report = await hosts.CheckAsync(env, refresh: true, ct);
            if (cache.ShouldAnnounce(env.Id, report))
            {
                announce.Add($"{env.Name}: {report.Summary(env.DockerVersion)}");
            }
        }

        if (announce.Count > 0)
        {
            try
            {
                await services.GetRequiredService<INotificationService>().NotifyAsync("MatDock: host updates available",
                    string.Join('\n', announce) + "\n\nUpdates page: Docker can be updated from there.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                services.GetRequiredService<ILogger<HostUpdateMonitor>>().LogInformation(ex, "Sending the host update notification failed.");
            }
        }

        return announce;
    }
}
