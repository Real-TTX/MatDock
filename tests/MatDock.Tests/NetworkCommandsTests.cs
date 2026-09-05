using MatDock.Core.Networks;
using Xunit;

namespace MatDock.Tests;

public class NetworkCommandsTests
{
    [Theory]
    [InlineData("bridge")]
    [InlineData("my-net")]
    [InlineData("app_net.1")]
    [InlineData("a1b2c3d4e5f6")] // hex id
    public void Accepts_valid_network_refs(string nameOrId)
        => Assert.True(NetworkCommands.IsValidNetworkRef(nameOrId));

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("net;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("a`id`")]
    [InlineData("a|b")]
    [InlineData("a/b")]
    public void Rejects_invalid_or_injection_refs(string nameOrId)
        => Assert.False(NetworkCommands.IsValidNetworkRef(nameOrId));

    [Fact]
    public void List_keeps_doubled_go_template_braces()
    {
        var cmd = NetworkCommands.List("docker");
        Assert.Contains("docker network ls --format '{{json .}}'", cmd);
        Assert.DoesNotContain("'{json .}'", cmd);
    }

    [Fact]
    public void ListDangling_lists_unused_network_names_only()
    {
        var cmd = NetworkCommands.ListDangling("docker");
        Assert.Contains("docker network ls -q --filter dangling=true --format '{{.Name}}'", cmd);
    }

    [Fact]
    public void Remove_builds_expected_and_validates()
    {
        Assert.Contains("docker network rm 'my-net'", NetworkCommands.Remove("my-net"));
        Assert.Throws<ArgumentException>(() => NetworkCommands.Remove("$(evil)"));
    }

    [Fact]
    public void Inspect_builds_expected_and_validates()
    {
        Assert.Contains("docker network inspect 'my-net' --format '{{json .}}'", NetworkCommands.InspectJson("my-net"));
        Assert.Throws<ArgumentException>(() => NetworkCommands.InspectJson("a;b"));
    }

    [Fact]
    public void Prune_removes_unused_networks_non_interactively()
    {
        var cmd = NetworkCommands.Prune("docker");
        Assert.Contains("docker network prune -f", cmd);
    }

    [Theory]
    [InlineData(false, null, "docker network prune -f")]
    [InlineData(true, null, "sudo -n docker network prune -f")]
    public void Prune_respects_docker_head(bool sudo, string? host, string expectedTail)
    {
        var head = MatDock.Core.Volumes.VolumeCommands.DockerHead(sudo, host);
        Assert.Contains(expectedTail, NetworkCommands.Prune(head));
    }
}
