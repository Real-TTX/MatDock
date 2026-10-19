using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MatDock.Core.Docker;

/// <summary>One reading of a host: CPU (load per core), memory and disk in percent.</summary>
public sealed record HostSample(DateTime TimeUtc, double Cpu, double Mem, double Disk);

/// <summary>
/// The last 24 hours of CPU, memory and disk per environment, in memory (a restart starts over): a reading at most every
/// <see cref="MinGap"/>, from the sampler in the background and from every page that reads the host anyway.
/// </summary>
public sealed class HostMetricsHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(4);

    private readonly ConcurrentDictionary<long, List<HostSample>> _samples = new();
    private readonly TimeProvider _time;

    public HostMetricsHistory(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Records <paramref name="stats"/> when they hold values and the last reading is old enough.</summary>
    public void Add(long environmentId, HostStats stats)
    {
        if (stats.Cores is null && stats.MemTotalBytes is null && stats.DiskTotalBytes is null)
        {
            return; // nothing was read
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var list = _samples.GetOrAdd(environmentId, _ => new List<HostSample>());
        lock (list)
        {
            if (list.Count > 0 && now - list[^1].TimeUtc < MinGap)
            {
                return;
            }

            list.Add(new HostSample(now, stats.CpuPercent, stats.MemPercent, stats.DiskPercent));
            list.RemoveAll(s => now - s.TimeUtc > Keep);
        }
    }

    public IReadOnlyList<HostSample> Get(long environmentId)
    {
        if (!_samples.TryGetValue(environmentId, out var list))
        {
            return Array.Empty<HostSample>();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        lock (list)
        {
            return list.Where(s => now - s.TimeUtc <= Keep).ToList();
        }
    }

    /// <summary>
    /// The points of an SVG polyline for percentages (0–100) over a box of <paramref name="width"/> × <paramref name="height"/>, oldest on the
    /// left; null for fewer than two values.
    /// </summary>
    public static string? Sparkline(IReadOnlyList<double> values, int width, int height)
    {
        if (values.Count < 2)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * (double)width / (values.Count - 1);
            var y = height - Math.Clamp(values[i], 0, 100) / 100.0 * height;
            sb.Append(i == 0 ? string.Empty : " ").Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.#", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
