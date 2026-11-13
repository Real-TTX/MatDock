using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Execution;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MatDock.Tests;

/// <summary>The CPU of a host is what its CPUs do, not its load; a container's CPU is a share of its host; slow readings do not hold up a page.</summary>
public class CpuReadingTests
{
    // Read on server2 (8 cores) half a second apart while its load was 5.28: the CPUs were 38.5 % busy, the load per core said 66 %.
    private const string Before = "cpu  38412744 1497 33089861 466298534 1740921 0 4629446 0 0 0";
    private const string After = "cpu  38412992 1497 33090104 466299334 1740927 0 4629460 0 0 0";

    [Fact]
    public void The_host_cpu_is_measured_and_not_the_load()
    {
        var stats = HostStats.Parse($"CPU1={Before}\nCPU2={After}\nCORES=8\nLOAD1=5.28\n");

        Assert.Equal(38.5, stats.CpuPercent, 1);
        Assert.True(stats.HasCpu);
    }

    [Fact]
    public void Without_a_measurement_the_load_per_core_stands_in()
    {
        var stats = HostStats.Parse("CORES=8\nLOAD1=5.28\n");

        Assert.Null(stats.CpuBusy);
        Assert.Equal(66.0, stats.CpuPercent, 1);
        Assert.False(HostStats.Parse("MEMTOTAL=100\n").HasCpu);
    }

    [Theory]
    [InlineData("cpu 100 0 100 800 0 0 0 0", "cpu 100 0 100 800 0 0 0 0", null)] // no time passed
    [InlineData("cpu 0 0 0 100 0 0 0 0", "cpu 50 0 50 100 0 0 0 0", 100.0)]
    [InlineData("cpu 0 0 0 0 0 0 0 0", "cpu 10 0 0 50 40 0 0 0", 10.0)] // waiting for a disk is not busy
    [InlineData("cpu0 1 2 3 4", "cpu0 2 3 4 5", null)] // a single core's line is not the total
    [InlineData("", "cpu 1 2 3 4 5", null)]
    public void Busy_counts_everything_but_idle_and_iowait(string first, string second, double? expected)
        => Assert.Equal(expected, HostStats.Busy(first, second));

    [Fact]
    public void The_measurement_is_part_of_the_host_command()
    {
        Assert.Contains("CPU1=$(head -n1 /proc/stat", HostStats.BuildCommand("docker"));
        Assert.Contains("CPU2=$(head -n1 /proc/stat", HostStats.BuildCommandInContainer("docker", "alpine:3"));
    }

    [Fact]
    public void A_container_on_two_of_eight_cores_is_a_quarter_of_its_host()
    {
        var containers = ContainerService.ParseContainers("""{"ID":"abc123def456","Names":"plex","State":"running"}""");
        ContainerService.ApplyStats(containers, """{"ID":"abc123def456","CPUPerc":"200.00%","MemPerc":"5.00%","MemUsage":"1GiB / 20GiB"}""" + "\nNCPU=8\n");

        Assert.Equal(200.0, containers[0].CpuPercent);
        Assert.Equal(8, containers[0].HostCores);
        Assert.Equal(25.0, containers[0].CpuOfHost);
    }

    [Fact]
    public void Without_the_core_count_the_cpu_stays_as_docker_said()
    {
        var containers = ContainerService.ParseContainers("""{"ID":"abc123def456","Names":"plex","State":"running"}""");
        ContainerService.ApplyStats(containers, """{"ID":"abc123def456","CPUPerc":"12.50%"}""");
        Assert.Equal(12.5, containers[0].CpuOfHost);
        Assert.EndsWith("info --format 'NCPU={{.NCPU}}' 2>/dev/null", ContainerCommands.StatsWithCores("docker"));
    }

    [Fact]
    public async Task A_stale_reading_is_shown_at_once_while_one_new_reading_is_taken()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = "t:" + Guid.NewGuid();
        var loads = 0;
        var gate = new TaskCompletionSource();

        async Task<int> Load(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref loads);
            if (n > 1)
            {
                await gate.Task; // the second reading is slow
            }

            return n;
        }

        Assert.Equal(1, await StaleCache.GetAsync(cache, key, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Load));

        // Stale (fresh is zero): the old value comes back at once, twice, and only one new reading runs.
        Assert.Equal(1, await StaleCache.GetAsync(cache, key, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Load));
        Assert.Equal(1, await StaleCache.GetAsync(cache, key, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Load));
        await WaitUntil(() => Volatile.Read(ref loads) == 2);
        await Task.Delay(50);
        Assert.Equal(2, loads);

        gate.SetResult();
        await WaitUntil(() => StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.Zero, Load).Result == 2);
    }

    [Fact]
    public async Task The_first_reading_is_waited_for_only_so_long()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = "t:" + Guid.NewGuid();
        var gate = new TaskCompletionSource();
        Task<string> Slow(CancellationToken ct) => gate.Task.ContinueWith(_ => "late", ct);

        Assert.Null(await StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100), Slow));

        gate.SetResult();
        await WaitUntil(() => StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.Zero, Slow).Result == "late");
    }

    [Fact]
    public async Task A_failed_reading_keeps_nothing_and_is_tried_again()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = "t:" + Guid.NewGuid();
        var calls = 0;
        Task<string> Load(CancellationToken ct) => ++calls == 1 ? throw new InvalidOperationException("host down") : Task.FromResult("up");

        Assert.Null(await StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Load, retryAfter: TimeSpan.Zero));
        Assert.Equal("up", await StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Load, retryAfter: TimeSpan.Zero));
    }

    [Fact]
    public async Task A_host_that_does_not_answer_is_left_alone_for_a_while_and_costs_the_next_pages_nothing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = "t:" + Guid.NewGuid();
        var calls = 0;
        Task<string> Down(CancellationToken ct) { calls++; throw new InvalidOperationException("host down"); }

        Assert.Null(await StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Down));

        // The next pages: no new reading and no wait (the load would hang, the wait is long: both would fail the test's time).
        var hang = new TaskCompletionSource<string>();
        var started = DateTime.UtcNow;
        Assert.Null(await StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), _ => { calls++; return hang.Task; }));
        Assert.Equal(1, calls);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));

        // Once the pause is over (forgotten here) it is asked again.
        StaleCache.Forget(cache, key);
        Assert.Equal("up", await StaleCache.GetAsync(cache, key, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), _ => Task.FromResult("up")));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
