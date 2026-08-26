using MatDock.Core.Docker;
using Xunit;

namespace MatDock.Tests;

public class HostStatsTests
{
    [Fact]
    public void Parse_reads_metrics_and_computes_percentages()
    {
        var output = "CORES=4\nLOAD1=2.00\nMEMTOTAL=8000000\nMEMAVAIL=2000000\nDISKTOTAL=100000\nDISKUSED=40000\nCONTAINERS=3\n";
        var s = HostStats.Parse(output);

        Assert.Equal(4, s.Cores);
        Assert.Equal(2.0, s.Load1);
        Assert.Equal(8000000L * 1024, s.MemTotalBytes);
        Assert.Equal((8000000L - 2000000L) * 1024, s.MemUsedBytes);
        Assert.Equal(3, s.RunningContainers);
        Assert.Equal(50, (int)System.Math.Round(s.CpuPercent));  // load 2 / 4 cores
        Assert.Equal(75, (int)System.Math.Round(s.MemPercent));  // 6M used / 8M
        Assert.Equal(40, (int)System.Math.Round(s.DiskPercent)); // 40k / 100k
    }

    [Fact]
    public void Parse_tolerates_missing_values()
    {
        var s = HostStats.Parse("CORES=\nLOAD1=\nCONTAINERS=0\n");
        Assert.Null(s.Cores);
        Assert.Null(s.MemTotalBytes);
        Assert.Equal(0, s.RunningContainers);
        Assert.Equal(0, s.CpuPercent);
    }
}
