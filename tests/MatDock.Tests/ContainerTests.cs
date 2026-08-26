using MatDock.Core.Containers;
using Xunit;

namespace MatDock.Tests;

public class ContainerTests
{
    [Theory]
    [InlineData("a1b2c3d4", true)]
    [InlineData("my_app-1.web", true)]
    [InlineData("evil; rm -rf /", false)]
    [InlineData("$(id)", false)]
    [InlineData("", false)]
    [InlineData("abc\n", false)]        // trailing newline must be rejected (\z not $)
    [InlineData("abc\nrm -rf /", false)]
    public void IsValidId_validates(string id, bool expected)
        => Assert.Equal(expected, ContainerCommands.IsValidId(id));

    [Fact]
    public void Action_and_Logs_build_and_reject_injection()
    {
        Assert.Contains("docker restart 'abc123'", ContainerCommands.Action("docker", ContainerAction.Restart, "abc123"));
        Assert.Contains("docker logs --tail 50 'abc123' 2>&1", ContainerCommands.Logs("docker", "abc123", 50));
        Assert.Throws<ArgumentException>(() => ContainerCommands.Action("docker", ContainerAction.Stop, "a; rm -rf /"));
    }

    [Fact]
    public void Logs_tail_is_clamped()
        => Assert.Contains("--tail 5000", ContainerCommands.Logs("docker", "abc", 999999));

    [Fact]
    public void ParseContainers_reads_state_and_compose_labels()
    {
        var line = "{\"ID\":\"abc123\",\"Names\":\"web-1\",\"Image\":\"nginx:latest\",\"State\":\"running\",\"Status\":\"Up 2 hours\",\"Ports\":\"0.0.0.0:80->80/tcp\",\"Labels\":\"com.docker.compose.project=shop,com.docker.compose.service=web,foo=bar\"}";
        var c = ContainerService.ParseContainers(line).Single();

        Assert.Equal("abc123", c.Id);
        Assert.Equal("web-1", c.Name);
        Assert.True(c.IsRunning);
        Assert.Equal("shop", c.Project);
        Assert.Equal("web", c.Service);
    }

    [Fact]
    public void ParseContainers_without_compose_labels_has_null_project()
    {
        var line = "{\"ID\":\"z1\",\"Names\":\"solo\",\"Image\":\"redis\",\"State\":\"exited\",\"Status\":\"Exited (0)\",\"Ports\":\"\",\"Labels\":\"foo=bar\"}";
        var c = ContainerService.ParseContainers(line).Single();

        Assert.False(c.IsRunning);
        Assert.Null(c.Project);
        Assert.Null(c.Service);
    }

    [Fact]
    public void ApplyStats_merges_cpu_and_mem_by_id()
    {
        var line = "{\"ID\":\"abc123\",\"Names\":\"web-1\",\"Image\":\"nginx\",\"State\":\"running\",\"Status\":\"Up\",\"Labels\":\"\"}";
        var containers = ContainerService.ParseContainers(line).ToList();

        var stats = "{\"ID\":\"abc123\",\"CPUPerc\":\"12.5%\",\"MemPerc\":\"3.25%\",\"MemUsage\":\"100MiB / 7.8GiB\"}\n"
                  + "{\"ID\":\"other\",\"CPUPerc\":\"99%\",\"MemPerc\":\"99%\",\"MemUsage\":\"x\"}";
        ContainerService.ApplyStats(containers, stats);

        var c = containers.Single();
        Assert.Equal(12.5, c.CpuPercent);
        Assert.Equal(3.25, c.MemPercent);
        Assert.Equal("100MiB / 7.8GiB", c.MemUsage);
    }

    [Fact]
    public void ApplyStats_matches_short_ps_id_against_full_stats_id()
    {
        // docker ps reports the 12-char short id; docker stats may report the full 64-char id.
        var shortId = "3f4a9c8b1d2e";
        var fullId = shortId + new string('0', 52); // 64 chars
        var line = $"{{\"ID\":\"{shortId}\",\"Names\":\"web\",\"Image\":\"nginx\",\"State\":\"running\",\"Status\":\"Up\",\"Labels\":\"\"}}";
        var containers = ContainerService.ParseContainers(line).ToList();

        ContainerService.ApplyStats(containers, $"{{\"ID\":\"{fullId}\",\"CPUPerc\":\"5.0%\",\"MemPerc\":\"1.0%\",\"MemUsage\":\"10MiB / 1GiB\"}}");

        Assert.Equal(5.0, containers.Single().CpuPercent);
        Assert.Equal("10MiB / 1GiB", containers.Single().MemUsage);
    }

    [Fact]
    public void ApplyStats_leaves_unmatched_containers_untouched()
    {
        var line = "{\"ID\":\"noStats\",\"Names\":\"db\",\"Image\":\"pg\",\"State\":\"exited\",\"Status\":\"Exited\",\"Labels\":\"\"}";
        var containers = ContainerService.ParseContainers(line).ToList();

        ContainerService.ApplyStats(containers, "{\"ID\":\"zzz\",\"CPUPerc\":\"1%\",\"MemPerc\":\"1%\",\"MemUsage\":\"x\"}");

        Assert.Null(containers.Single().CpuPercent);
        Assert.Null(containers.Single().MemUsage);
    }

    [Fact]
    public void ParseContainers_skips_malformed_and_non_object_lines_but_keeps_valid()
    {
        // A stray non-object JSON line and a non-string field must NOT abort the whole list.
        var output = string.Join('\n', new[]
        {
            "5",
            "not json at all",
            "{\"ID\":\"good\",\"Names\":\"web\",\"Image\":\"nginx\",\"State\":\"running\",\"Status\":\"Up\",\"Labels\":123}",
        });

        var list = ContainerService.ParseContainers(output);

        var c = Assert.Single(list);
        Assert.Equal("good", c.Id);
        Assert.True(c.IsRunning);
        Assert.Null(c.Project); // Labels was a number, treated as absent
    }
}
