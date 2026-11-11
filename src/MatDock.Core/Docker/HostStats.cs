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

    /// <summary>How busy the CPUs were, measured from <c>/proc/stat</c> over half a second (null when the host did not say).</summary>
    public double? CpuBusy { get; init; }

    /// <summary>
    /// CPU of the host in percent: the measured busy share. The load per core only stands in where there is no measurement: the load also counts
    /// processes waiting for a disk or a network share, so on a host with a NAS it is far above what the CPUs do.
    /// </summary>
    public double CpuPercent => CpuBusy is { } busy ? Math.Clamp(busy, 0, 100) : Cores is > 0 && Load1 is { } l ? Math.Clamp(l / Cores.Value * 100.0, 0, 100) : 0;

    /// <summary>Whether there is a CPU reading at all.</summary>
    public bool HasCpu => CpuBusy is not null || (Cores is > 0 && Load1 is not null);
    public double MemPercent => MemTotalBytes is > 0 ? Math.Clamp((MemUsedBytes ?? 0) / (double)MemTotalBytes.Value * 100.0, 0, 100) : 0;
    public double DiskPercent => DiskTotalBytes is > 0 ? Math.Clamp((DiskUsedBytes ?? 0) / (double)DiskTotalBytes.Value * 100.0, 0, 100) : 0;

    /// <summary>The exact shell command whose KEY=VALUE output <see cref="Parse"/> understands.</summary>
    public static string BuildCommand(string dockerHead) =>
        MatDock.Core.Volumes.VolumeCommands.PathPrefix +
        CpuSample +
        "echo CORES=$(nproc 2>/dev/null); " +
        "echo LOAD1=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null); " +
        "echo MEMTOTAL=$(awk '/^MemTotal:/{print $2}' /proc/meminfo 2>/dev/null); " +
        "echo MEMAVAIL=$(awk '/^MemAvailable:/{print $2}' /proc/meminfo 2>/dev/null); " +
        "echo DISKTOTAL=$(df -kP / 2>/dev/null | awk 'NR==2{print $2}'); " +
        "echo DISKUSED=$(df -kP / 2>/dev/null | awk 'NR==2{print $3}'); " +
        $"echo CONTAINERS=$({dockerHead} ps -q 2>/dev/null | wc -l | tr -d ' ')";

    /// <summary>
    /// The same readings from a throwaway <paramref name="helperImage"/> container on the Docker host (for a remote Docker API, where the commands run
    /// on the MatDock host): /proc/loadavg and /proc/meminfo inside a container are the host's, <c>df /</c> is the disk of Docker's data.
    /// </summary>
    public static string BuildCommandInContainer(string dockerHead, string helperImage)
    {
        const string inner =
            CpuSample +
            "echo CORES=$(nproc 2>/dev/null); " +
            "echo LOAD1=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null); " +
            "echo MEMTOTAL=$(awk '/^MemTotal:/{print $2}' /proc/meminfo 2>/dev/null); " +
            "echo MEMAVAIL=$(awk '/^MemAvailable:/{print $2}' /proc/meminfo 2>/dev/null); " +
            "echo DISKTOTAL=$(df -kP / 2>/dev/null | awk 'NR==2{print $2}'); " +
            "echo DISKUSED=$(df -kP / 2>/dev/null | awk 'NR==2{print $3}')";
        return MatDock.Core.Volumes.VolumeCommands.PathPrefix + dockerHead + " run --rm " + MatDock.Core.Volumes.VolumeFileCommands.ShellQuote(helperImage)
               + " sh -c " + MatDock.Core.Volumes.VolumeFileCommands.ShellQuote(inner) + " 2>/dev/null; "
               + $"echo CONTAINERS=$({dockerHead} ps -q 2>/dev/null | wc -l | tr -d ' ')";
    }

    /// <summary>The CPU counters of the host twice, half a second apart (busybox and coreutils both sleep fractions).</summary>
    private const string CpuSample = "echo CPU1=$(head -n1 /proc/stat 2>/dev/null); sleep 0.5; echo CPU2=$(head -n1 /proc/stat 2>/dev/null); ";

    /// <summary>The busy share between two <c>cpu</c> lines of <c>/proc/stat</c> (user nice system idle iowait irq softirq steal …); idle and iowait are not busy.</summary>
    public static double? Busy(string? first, string? second)
    {
        static long[]? Ticks(string? line)
        {
            var parts = (line ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu")
            {
                return null;
            }

            // guest and guest_nice are already inside user and nice.
            var values = parts.Skip(1).Take(8).Select(p => long.TryParse(p, out var v) ? v : -1).ToArray();
            return values.Any(v => v < 0) ? null : values;
        }

        if (Ticks(first) is not { } a || Ticks(second) is not { } b)
        {
            return null;
        }

        var total = b.Sum() - a.Sum();
        var idle = b[3] - a[3] + (b.Length > 4 && a.Length > 4 ? b[4] - a[4] : 0);
        return total > 0 ? Math.Clamp((total - idle) * 100.0 / total, 0, 100) : null;
    }

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
            CpuBusy = Busy(map.GetValueOrDefault("CPU1"), map.GetValueOrDefault("CPU2")),
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
