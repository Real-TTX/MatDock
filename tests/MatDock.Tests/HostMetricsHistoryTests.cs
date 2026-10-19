using MatDock.Core.Docker;
using Xunit;

namespace MatDock.Tests;

public class HostMetricsHistoryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HostStats Stats(double load) => new() { Cores = 4, Load1 = load, MemTotalBytes = 100, MemUsedBytes = 50, DiskTotalBytes = 10, DiskUsedBytes = 1 };

    [Fact]
    public void Keeps_one_reading_per_few_minutes_for_a_day()
    {
        var clock = new Clock();
        var history = new HostMetricsHistory(clock);
        history.Add(1, Stats(1));
        clock.Now += TimeSpan.FromMinutes(1);
        history.Add(1, Stats(2)); // too soon: ignored
        clock.Now += TimeSpan.FromMinutes(5);
        history.Add(1, Stats(2));
        history.Add(2, new HostStats()); // nothing read: ignored

        var samples = history.Get(1);
        Assert.Equal(2, samples.Count);
        Assert.Equal(25, samples[0].Cpu);
        Assert.Equal(50, samples[1].Cpu);
        Assert.Equal(50, samples[1].Mem);
        Assert.Empty(history.Get(2));

        clock.Now += TimeSpan.FromHours(25);
        Assert.Empty(history.Get(1));
    }

    [Fact]
    public void The_sparkline_spans_the_box()
    {
        Assert.Null(HostMetricsHistory.Sparkline([10], 200, 32));
        Assert.Equal("0,32 100,16 200,0", HostMetricsHistory.Sparkline([0, 50, 100], 200, 32));
    }
}
