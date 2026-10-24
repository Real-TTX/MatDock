using System.Globalization;
using MatDock.Core.Docker;
using MatDock.Core.Entities;

namespace MatDock.Core.Notifications;

/// <summary>The limits that apply to one host: its own (0 = none), else the global ones of the notification settings.</summary>
public sealed record HostLimits(int? Cpu, int? Mem, int? Disk)
{
    public static HostLimits Of(NotificationSettings settings, DockerEnvironment environment)
        => new(Pick(environment.AlertCpuPercent, settings.HostCpuLimit), Pick(environment.AlertMemPercent, settings.HostMemLimit), Pick(environment.AlertDiskPercent, settings.HostDiskLimit));

    private static int? Pick(int? own, int? global) => own is { } o ? (o is > 0 and <= 100 ? o : null) : global is > 0 and <= 100 ? global : null;
}

/// <summary>
/// Watches the readings of the hosts against their limits and says when one goes over (CPU and memory after two readings in a row, the disk at once) and
/// when it is back (3 points below the limit, so a value that wobbles around it does not report every reading). One message per crossing.
/// </summary>
public sealed class HostLimitWatch
{
    /// <summary>A value has to fall this many points below the limit to count as back.</summary>
    public const int Hysteresis = 3;

    private readonly Dictionary<(long Env, string Metric), (int Over, bool Alerted)> _state = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Check(DockerEnvironment environment, HostStats stats, HostLimits limits)
    {
        var messages = new List<string>();
        lock (_gate)
        {
            Evaluate(messages, environment, "CPU", stats.CpuPercent, limits.Cpu, readings: 2, valid: stats.Cores is > 0 && stats.Load1 is not null);
            Evaluate(messages, environment, "Memory", stats.MemPercent, limits.Mem, readings: 2, valid: stats.MemTotalBytes is > 0);
            Evaluate(messages, environment, "Disk", stats.DiskPercent, limits.Disk, readings: 1, valid: stats.DiskTotalBytes is > 0);
        }

        return messages;
    }

    private void Evaluate(List<string> messages, DockerEnvironment environment, string metric, double value, int? limit, int readings, bool valid)
    {
        var key = (environment.Id, metric);
        if (!valid || limit is not { } max)
        {
            _state.Remove(key);
            return;
        }

        var (over, alerted) = _state.TryGetValue(key, out var s) ? s : (0, false);
        var shown = value.ToString("0", CultureInfo.InvariantCulture);
        if (value > max)
        {
            over++;
            if (!alerted && over >= readings)
            {
                alerted = true;
                messages.Add($"{metric} of {environment.Name} at {shown} % (limit {max} %).");
            }
        }
        else if (value <= max - Hysteresis)
        {
            if (alerted)
            {
                messages.Add($"{metric} of {environment.Name} back at {shown} % (limit {max} %).");
            }

            over = 0;
            alerted = false;
        }
        else if (!alerted)
        {
            over = 0;
        }

        _state[key] = (over, alerted);
    }
}

/// <summary>A container that started, read from <c>docker events</c>.</summary>
public sealed record ContainerStart(string Id, string Name, DateTime TimeUtc);

/// <summary>
/// Counts the starts of each container over a sliding window and says when one starts <c>count</c> times within it (a restart policy that brings back a
/// container that keeps crashing). Said once per window and container, then again only when the loop goes on after the window.
/// </summary>
public sealed class RestartLoopWatch
{
    private readonly Dictionary<(long Env, string Id), List<DateTime>> _starts = new();
    private readonly Dictionary<(long Env, string Id), DateTime> _reported = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Add(DockerEnvironment environment, IEnumerable<ContainerStart> starts, int count, TimeSpan window, DateTime nowUtc)
    {
        var messages = new List<string>();
        if (count < 2)
        {
            return messages;
        }

        lock (_gate)
        {
            foreach (var group in starts.GroupBy(s => s.Id))
            {
                var key = (environment.Id, group.Key);
                if (!_starts.TryGetValue(key, out var times))
                {
                    _starts[key] = times = [];
                }

                times.AddRange(group.Select(s => s.TimeUtc));
                times.RemoveAll(t => nowUtc - t > window);
                if (times.Count >= count && (!_reported.TryGetValue(key, out var last) || nowUtc - last > window))
                {
                    _reported[key] = nowUtc;
                    var name = group.Last().Name;
                    messages.Add($"Container {name} on {environment.Name} started {times.Count} times in {window.TotalMinutes:0} minutes (a restart loop).");
                }
            }

            // Forget what left the window, so the watch does not grow with every container that ever started.
            foreach (var stale in _starts.Where(p => p.Value.TrueForAll(t => nowUtc - t > window)).Select(p => p.Key).ToList())
            {
                _starts.Remove(stale);
            }

            foreach (var stale in _reported.Where(p => nowUtc - p.Value > window * 2).Select(p => p.Key).ToList())
            {
                _reported.Remove(stale);
            }
        }

        return messages;
    }
}
