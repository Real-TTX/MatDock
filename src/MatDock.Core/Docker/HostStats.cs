using System.Globalization;

namespace MatDock.Core.Docker;

/// <summary>Basic host resource metrics gathered over SSH (CPU load, memory, disk, containers).</summary>
public sealed class HostStats
{
    public int? Cores { get; init; }
    public double? Load1 { get; init; }
    public long? MemTotalBytes { get; init; }
    public long? MemUsedBytes { get; init; }
    public long? DiskTotalBytes { get; init; }
    public long? DiskUsedBytes { get; init; }
    public int? RunningContainers { get; init; }

    public double CpuPercent => Cores is > 0 && Load1 is { } l ? Math.Clamp(l / Cores.Value * 100.0, 0, 100) : 0;
    public double MemPercent => MemTotalBytes is > 0 ? Math.Clamp((MemUsedBytes ?? 0) / (double)MemTotalBytes.Value * 100.0, 0, 100) : 0;
    public double DiskPercent => DiskTotalBytes is > 0 ? Math.Clamp((DiskUsedBytes ?? 0) / (double)DiskTotalBytes.Value * 100.0, 0, 100) : 0;

    /// <summary>The exact shell command whose KEY=VALUE output <see cref="Parse"/> understands.</summary>
    public static string BuildCommand(string dockerHead) =>
        MatDock.Core.Volumes.VolumeCommands.PathPrefix +
        "echo CORES=$(nproc 2>/dev/null); " +
        "echo LOAD1=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null); " +
        "echo MEMTOTAL=$(awk '/^MemTotal:/{print $2}' /proc/meminfo 2>/dev/null); " +
        "echo MEMAVAIL=$(awk '/^MemAvailable:/{print $2}' /proc/meminfo 2>/dev/null); " +
        "echo DISKTOTAL=$(df -kP / 2>/dev/null | awk 'NR==2{print $2}'); " +
        "echo DISKUSED=$(df -kP / 2>/dev/null | awk 'NR==2{print $3}'); " +
        $"echo CONTAINERS=$({dockerHead} ps -q 2>/dev/null | wc -l | tr -d ' ')";

    public static HostStats Parse(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        int? cores = TryInt(map, "CORES");
        long? memTotalKb = TryLong(map, "MEMTOTAL");
        long? memAvailKb = TryLong(map, "MEMAVAIL");
        long? diskTotalKb = TryLong(map, "DISKTOTAL");
        long? diskUsedKb = TryLong(map, "DISKUSED");

        return new HostStats
        {
            Cores = cores,
            Load1 = TryDouble(map, "LOAD1"),
            MemTotalBytes = memTotalKb * 1024,
            MemUsedBytes = memTotalKb is { } t && memAvailKb is { } a ? (t - a) * 1024 : null,
            DiskTotalBytes = diskTotalKb * 1024,
            DiskUsedBytes = diskUsedKb * 1024,
            RunningContainers = TryInt(map, "CONTAINERS")
        };
    }

    private static int? TryInt(IReadOnlyDictionary<string, string> m, string k)
        => m.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : null;

    private static long? TryLong(IReadOnlyDictionary<string, string> m, string k)
        => m.TryGetValue(k, out var v) && long.TryParse(v, out var l) ? l : null;

    private static double? TryDouble(IReadOnlyDictionary<string, string> m, string k)
        => m.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
