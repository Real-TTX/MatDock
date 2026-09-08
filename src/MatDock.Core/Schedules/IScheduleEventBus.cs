using MatDock.Core.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Schedules;

/// <summary>
/// In-process bus that lets any component signal an internal event (deploy/backup/sync failed,
/// environment offline, …). Event-triggered <see cref="ScheduledTask"/>s subscribed to that event
/// then run. Publishing is best-effort and never throws into the caller.
/// </summary>
public interface IScheduleEventBus
{
    Task PublishAsync(ScheduleEvent evt, long? environmentId = null, string? detail = null, CancellationToken ct = default);
}

/// <summary>Singleton bus: opens a scope per event and dispatches to <see cref="ScheduleService"/>.</summary>
public sealed class ScheduleEventBus : IScheduleEventBus
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduleEventBus> _logger;

    public ScheduleEventBus(IServiceScopeFactory scopeFactory, ILogger<ScheduleEventBus> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task PublishAsync(ScheduleEvent evt, long? environmentId = null, string? detail = null, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ScheduleService>();
            await service.DispatchEventAsync(evt, environmentId, detail, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dispatching schedule event {Event} failed.", evt);
        }
    }
}
