using MatDock.Core.Containers;
using MatDock.Core.Networks;
using Xunit;

namespace MatDock.Tests;

public class NetworkMapTests
{
    private static DockerContainer C(string id, string name, string state, params string[] networks)
        => new() { Id = id, Name = name, State = state, Image = "img", Networks = networks };

    [Fact]
    public void Containers_are_read_with_their_networks()
    {
        var list = ContainerService.ParseContainers("""{"ID":"a1","Names":"web","State":"running","Networks":"shop_default,proxy"}""");
        Assert.Equal(["shop_default", "proxy"], Assert.Single(list).Networks);
    }

    [Fact]
    public void Networks_left_containers_right_one_line_per_attachment()
    {
        var map = NetworkMap.Build(
        [
            C("1", "shop-db", "running", "shop_default"),
            C("2", "shop-web", "running", "shop_default", "proxy"),
            C("3", "caddy", "running", "proxy"),
            C("4", "loose", "exited"),
        ]);

        Assert.Equal(["proxy", "shop_default"], map.Networks.Select(n => n.Label));
        Assert.Equal(3, map.Containers.Count);                     // the one without a network is not drawn
        Assert.Equal(4, map.Edges.Count);
        // Ordered by the networks they hang on: caddy (proxy) first, shop-web (both) between, shop-db last.
        Assert.Equal(["caddy", "shop-web", "shop-db"], map.Containers.Select(c => c.Label));
        Assert.Equal(2, map.Containers.Single(c => c.Label == "shop-web").Links);
        Assert.All(map.Networks, n => Assert.True(n.X < map.Containers[0].X));
        Assert.StartsWith("M", map.Edges[0].Path);
        Assert.DoesNotContain(",", map.Edges[0].Path.Split(' ')[0].Replace("M", "").Split(',')[0]); // invariant numbers
    }

    [Fact]
    public void An_empty_host_has_an_empty_map()
    {
        var map = NetworkMap.Build([]);
        Assert.Empty(map.Networks);
        Assert.Empty(map.Edges);
    }
}
