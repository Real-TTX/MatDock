using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests;

/// <summary>The readings of hosts and containers: written every five minutes, folded to one per hour after two days, gone after 30; and the Prometheus text.</summary>
public sealed class MetricsTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static HostStats Host(double load) => new() { Cores = 2, Load1 = load, MemTotalBytes = 1000, MemUsedBytes = 250, DiskTotalBytes = 100, DiskUsedBytes = 40 };

    private static DockerContainer Container(string name, string state = "running", double? cpu = 12.5, string? mem = "100MiB / 1GiB")
        => new() { Id = name + "id", Name = name, Image = "nginx:1.27", State = state, Project = "shop", CpuPercent = cpu, MemPercent = 10, MemUsage = mem };

    [Theory]
    [InlineData("100MiB / 7.8GiB", 104857600L)]
    [InlineData("1.5GB / 2GB", 1500000000L)]
    [InlineData("512B / 1KiB", 512L)]
    [InlineData("2.25kB / 1MB", 2250L)]
    [InlineData("--", null)]
    [InlineData(null, null)]
    public void The_memory_of_a_container_is_read_from_its_text(string? text, long? bytes)
        => Assert.Equal(bytes, MemoryText.UsedBytes(text));

    [Fact]
    public async Task A_reading_holds_the_host_and_its_running_containers()
    {
        using var db = new TestDatabase();
        var store = new MetricsStore(db.Context);

        await store.RecordAsync(7, Host(1), [Container("web"), Container("old", state: "exited"), Container("nostats", cpu: null, mem: null)], Now);

        var host = Assert.Single(await store.GetAsync(7, null, TimeSpan.FromHours(1), Now));
        Assert.Equal((50d, 25d, 40d, 250L), (host.Cpu, host.Mem, host.Disk!.Value, host.MemBytes!.Value));
        var web = Assert.Single(await store.GetAsync(7, "web", TimeSpan.FromHours(1), Now));
        Assert.Equal((12.5, 104857600L), (web.Cpu, web.MemBytes!.Value));
        Assert.Empty(await store.GetAsync(7, "old", TimeSpan.FromHours(1), Now));
        Assert.Equal(3, db.NewContext().MetricSamples.Count()); // the host, web, and nostats (it has a memory percentage)
    }

    [Fact]
    public async Task Old_readings_are_folded_per_hour_and_trimmed_after_30_days()
    {
        using var db = new TestDatabase();
        var store = new MetricsStore(db.Context);
        // Three days ago, 10:00 to 10:55: twelve readings with a load from 0 to 1.1 (CPU 0 % to 55 %).
        var old = Now.AddDays(-3).Date.AddHours(10);
        for (var i = 0; i < 12; i++)
        {
            await store.RecordAsync(1, Host(i / 10.0), [], old.AddMinutes(i * 5));
        }

        await store.RecordAsync(1, Host(1), [], Now.AddDays(-40));
        await store.RecordAsync(1, Host(1), [], Now.AddMinutes(-5));

        var removed = await store.CompactAsync(Now);

        Assert.Equal(13, removed); // 12 folded into one, 1 expired
        var rows = db.NewContext().MetricSamples.OrderBy(s => s.TimeUtc).ToList();
        Assert.Equal(2, rows.Count);
        var hour = rows[0];
        Assert.True(hour.Hourly);
        Assert.Equal(old.AddMinutes(30), hour.TimeUtc);
        Assert.Equal(27.5, hour.Cpu, precision: 3);
        Assert.False(rows[1].Hourly);

        // Compacting again changes nothing.
        Assert.Equal(0, await store.CompactAsync(Now));

        var week = await store.GetAsync(1, null, TimeSpan.FromDays(7), Now);
        Assert.Equal(2, week.Count);
    }

    [Fact]
    public void The_prometheus_text_has_a_line_per_value_with_escaped_labels()
    {
        var env = new DockerEnvironment { Id = 1, Name = "prod \"eu\"", Status = EnvironmentStatus.Online };
        var off = new DockerEnvironment { Id = 2, Name = "lab", Status = EnvironmentStatus.Offline };
        var reading = new MetricsSnapshot.Reading(env, DateTime.UtcNow, Host(1), [Container("web"), Container("old", state: "exited")]);

        var text = PrometheusText.Write([env, off], [reading]);

        Assert.Contains("# TYPE matdock_environment_up gauge", text);
        Assert.Contains("matdock_environment_up{environment=\"prod \\\"eu\\\"\"} 1\n", text);
        Assert.Contains("matdock_environment_up{environment=\"lab\"} 0\n", text);
        Assert.Contains("matdock_host_cpu_percent{environment=\"prod \\\"eu\\\"\"} 50\n", text);
        Assert.Contains("matdock_containers_running{environment=\"prod \\\"eu\\\"\"} 1\n", text);
        Assert.Contains("matdock_container_up{environment=\"prod \\\"eu\\\"\",container=\"old\",stack=\"shop\",image=\"nginx:1.27\"} 0\n", text);
        Assert.Contains("matdock_container_memory_bytes{environment=\"prod \\\"eu\\\"\",container=\"web\",stack=\"shop\",image=\"nginx:1.27\"} 104857600\n", text);
        Assert.DoesNotContain("container=\"old\",stack=\"shop\",image=\"nginx:1.27\"} 12.5", text);
    }
}
