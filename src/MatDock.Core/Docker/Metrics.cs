using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MatDock.Core.Containers;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Docker;

/// <summary>
/// One reading of a host (<see cref="Container"/> null) or of a container: CPU and memory in percent, the memory in bytes, the disk of a host. Readings
/// are taken every five minutes and kept for two days; older ones are folded into one per hour (<see cref="Hourly"/>) and kept for 30 days. Telemetry,
/// not records: no audit columns and no soft delete, what is out of the window is gone. Mapped to the <c>MetricSample</c> table.
/// </summary>
public class MetricSample
{
    public long Id { get; set; }
    public long EnvironmentId { get; set; }
    public string? Container { get; set; }
    public DateTime TimeUtc { get; set; }
    public double Cpu { get; set; }
    public double Mem { get; set; }
    public long? MemBytes { get; set; }
    public double? Disk { get; set; }
    public bool Hourly { get; set; }
}

/// <summary>A point of a chart.</summary>
public sealed record MetricPoint(DateTime TimeUtc, double Cpu, double Mem, long? MemBytes, double? Disk);

/// <summary>The readings of the hosts and containers in the database: written by the sampler, folded and trimmed once an hour, read by the charts.</summary>
public sealed class MetricsStore
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    public static readonly TimeSpan RawKeep = TimeSpan.FromHours(48);

    /// <summary>The ranges a chart offers.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Ranges = new Dictionary<string, TimeSpan>
    {
        ["6h"] = TimeSpan.FromHours(6),
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
    };

    private readonly MatDockDbContext _db;

    public MetricsStore(MatDockDbContext db) => _db = db;

    public static TimeSpan RangeOf(string? key) => key is not null && Ranges.TryGetValue(key, out var range) ? range : Ranges["24h"];

    /// <summary>Writes a reading of a host and of its running containers (those with stats).</summary>
    public async Task RecordAsync(long environmentId, HostStats host, IReadOnlyList<DockerContainer> containers, DateTime nowUtc, CancellationToken ct = default)
    {
        if (host.Cores is not null || host.MemTotalBytes is not null || host.DiskTotalBytes is not null)
        {
            _db.MetricSamples.Add(new MetricSample
            {
                EnvironmentId = environmentId, TimeUtc = nowUtc, Cpu = host.CpuPercent, Mem = host.MemPercent, MemBytes = host.MemUsedBytes,
                Disk = host.DiskTotalBytes is > 0 ? host.DiskPercent : null,
            });
        }

        foreach (var c in containers.Where(c => c.IsRunning && (c.CpuPercent is not null || c.MemPercent is not null)))
        {
            _db.MetricSamples.Add(new MetricSample
            {
                EnvironmentId = environmentId, Container = c.Name, TimeUtc = nowUtc, Cpu = c.CpuPercent ?? 0, Mem = c.MemPercent ?? 0, MemBytes = MemoryText.UsedBytes(c.MemUsage),
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The points of a host (<paramref name="container"/> null) or of a container over the last <paramref name="range"/>, oldest first.</summary>
    public async Task<IReadOnlyList<MetricPoint>> GetAsync(long environmentId, string? container, TimeSpan range, DateTime nowUtc, CancellationToken ct = default)
    {
        var from = nowUtc - range;
        // A long range reads the hourly points only (the raw ones of the last two days are folded the same way when shown).
        var hourly = range > RawKeep;
        var rows = await _db.MetricSamples.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId && s.Container == container && s.TimeUtc >= from)
            .OrderBy(s => s.TimeUtc)
            .Select(s => new { s.TimeUtc, s.Cpu, s.Mem, s.MemBytes, s.Disk, s.Hourly })
            .ToListAsync(ct);

        var points = rows.Select(r => new MetricPoint(r.TimeUtc, r.Cpu, r.Mem, r.MemBytes, r.Disk));
        if (hourly)
        {
            points = points.GroupBy(p => HourOf(p.TimeUtc))
                .Select(g => new MetricPoint(g.Key.AddMinutes(30), g.Average(p => p.Cpu), g.Average(p => p.Mem), Average(g.Select(p => p.MemBytes)), AverageOf(g.Select(p => p.Disk))));
        }

        return points.ToList();
    }

    /// <summary>Folds the readings older than two days into one per hour and deletes what is older than 30 days. Answers how many rows went.</summary>
    public async Task<int> CompactAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        // What is out of the window goes first (it is not worth folding); then whole hours only, so an hour is never folded twice.
        var expired = await _db.MetricSamples.Where(s => s.TimeUtc < nowUtc - Keep).ExecuteDeleteAsync(ct);
        var cutoff = HourOf(nowUtc - RawKeep);
        var raw = await _db.MetricSamples.Where(s => !s.Hourly && s.TimeUtc < cutoff).ToListAsync(ct);
        foreach (var g in raw.GroupBy(s => (s.EnvironmentId, s.Container, Hour: HourOf(s.TimeUtc))))
        {
            _db.MetricSamples.Add(new MetricSample
            {
                EnvironmentId = g.Key.EnvironmentId, Container = g.Key.Container, TimeUtc = g.Key.Hour.AddMinutes(30), Hourly = true,
                Cpu = g.Average(s => s.Cpu), Mem = g.Average(s => s.Mem), MemBytes = Average(g.Select(s => s.MemBytes)), Disk = AverageOf(g.Select(s => s.Disk)),
            });
        }

        _db.MetricSamples.RemoveRange(raw);
        await _db.SaveChangesAsync(ct);
        return raw.Count + expired;
    }

    private static DateTime HourOf(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    private static long? Average(IEnumerable<long?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : (long)list.Average();
    }

    private static double? AverageOf(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }
}

/// <summary>The memory a container uses, from the text of <c>docker stats</c> ("100MiB / 7.8GiB").</summary>
public static partial class MemoryText
{
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([KMGTP]?i?B)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Used();

    public static long? UsedBytes(string? memUsage)
    {
        var m = memUsage is null ? null : Used().Match(memUsage);
        if (m is null || !m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = m.Groups[2].Value.ToUpperInvariant();
        var binary = unit.Contains('I');
        double factor = unit[0] switch
        {
            'K' => binary ? 1024 : 1000,
            'M' => binary ? 1024d * 1024 : 1e6,
            'G' => binary ? 1024d * 1024 * 1024 : 1e9,
            'T' => binary ? 1024d * 1024 * 1024 * 1024 : 1e12,
            'P' => binary ? Math.Pow(1024, 5) : 1e15,
            _ => 1,
        };
        return (long)(value * factor);
    }
}

/// <summary>The last reading of each environment, for <c>/metrics</c>: kept in memory by the sampler, so a scrape never asks the hosts.</summary>
public sealed class MetricsSnapshot
{
    public sealed record Reading(DockerEnvironment Environment, DateTime TimeUtc, HostStats Host, IReadOnlyList<DockerContainer> Containers);

    private readonly ConcurrentDictionary<long, Reading> _readings = new();

    public void Set(Reading reading) => _readings[reading.Environment.Id] = reading;

    public void Remove(long environmentId) => _readings.TryRemove(environmentId, out _);

    public IReadOnlyList<Reading> All => _readings.Values.OrderBy(r => r.Environment.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>The Prometheus text format (version 0.0.4) of the environments and their last readings.</summary>
public static class PrometheusText
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Write(IReadOnlyList<DockerEnvironment> environments, IReadOnlyList<MetricsSnapshot.Reading> readings)
    {
        var sb = new StringBuilder();
        void Help(string name, string help, string type = "gauge") => sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n').Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        void Line(string name, string labels, double value) => sb.Append(name).Append('{').Append(labels).Append("} ").Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');

        Help("matdock_environment_up", "1 when the environment answered its last check.");
        foreach (var env in environments)
        {
            Line("matdock_environment_up", Env(env), env.Status == EnvironmentStatus.Online ? 1 : 0);
        }

        var hosts = readings.Where(r => r.Host.Cores is not null || r.Host.MemTotalBytes is not null).ToList();
        Help("matdock_host_cpu_percent", "CPU of the host (busy share of all cores over half a second), percent.");
        hosts.ForEach(r => Line("matdock_host_cpu_percent", Env(r.Environment), r.Host.CpuPercent));
        Help("matdock_host_memory_percent", "Memory of the host in use, percent.");
        hosts.ForEach(r => Line("matdock_host_memory_percent", Env(r.Environment), r.Host.MemPercent));
        Help("matdock_host_disk_percent", "Disk of the host (Docker's data) in use, percent.");
        hosts.Where(r => r.Host.DiskTotalBytes is > 0).ToList().ForEach(r => Line("matdock_host_disk_percent", Env(r.Environment), r.Host.DiskPercent));

        Help("matdock_containers_running", "Running containers.");
        readings.ToList().ForEach(r => Line("matdock_containers_running", Env(r.Environment), r.Containers.Count(c => c.IsRunning)));
        Help("matdock_container_up", "1 when the container runs.");
        foreach (var r in readings)
        {
            foreach (var c in r.Containers)
            {
                Line("matdock_container_up", Container(r.Environment, c), c.IsRunning ? 1 : 0);
            }
        }

        Help("matdock_container_cpu_percent", "CPU of the container, percent of one core.");
        Help("matdock_container_memory_percent", "Memory of the container, percent of its limit.");
        Help("matdock_container_memory_bytes", "Memory the container uses, bytes.");
        foreach (var r in readings)
        {
            foreach (var c in r.Containers.Where(c => c.IsRunning))
            {
                if (c.CpuPercent is { } cpu)
                {
                    Line("matdock_container_cpu_percent", Container(r.Environment, c), cpu);
                }

                if (c.MemPercent is { } mem)
                {
                    Line("matdock_container_memory_percent", Container(r.Environment, c), mem);
                }

                if (MemoryText.UsedBytes(c.MemUsage) is { } bytes)
                {
                    Line("matdock_container_memory_bytes", Container(r.Environment, c), bytes);
                }
            }
        }

        Help("matdock_reading_age_seconds", "Seconds since the last reading of the environment.");
        var now = DateTime.UtcNow;
        readings.ToList().ForEach(r => Line("matdock_reading_age_seconds", Env(r.Environment), Math.Max(0, (now - r.TimeUtc).TotalSeconds)));
        return sb.ToString();
    }

    private static string Env(DockerEnvironment env) => $"environment=\"{Escape(env.Name)}\"";

    private static string Container(DockerEnvironment env, DockerContainer c)
        => $"{Env(env)},container=\"{Escape(c.Name)}\",stack=\"{Escape(c.Project ?? string.Empty)}\",image=\"{Escape(c.Image)}\"";

    /// <summary>A label value: backslash, quote and line break escaped.</summary>
    public static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
