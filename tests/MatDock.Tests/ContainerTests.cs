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
}
