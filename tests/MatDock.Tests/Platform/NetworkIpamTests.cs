using System.Net;
using MatDock.Core.Networks;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>A new network with its own addresses (subnet, gateway, range, IPv6, the parent of a macvlan), and a container connected with a fixed address and more names.</summary>
public sealed class NetworkIpamTests : IClassFixture<NetworkIpamTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("net-host");
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public NetworkIpamTests(Site site) => _site = site;

    [Fact]
    public void The_addresses_become_arguments_of_network_create()
    {
        var ipam = new NetworkIpam("172.30.0.0/24", "172.30.0.1", "172.30.0.128/25", true, "fd00:dead:beef::/64", null);

        Assert.Null(ipam.Problem("bridge"));
        Assert.EndsWith("network create --driver bridge --subnet '172.30.0.0/24' --gateway '172.30.0.1' --ip-range '172.30.0.128/25' --ipv6 --subnet 'fd00:dead:beef::/64' 'app-net'",
            NetworkCommands.Create("app-net", "bridge", false, "docker", ipam));
        Assert.EndsWith("network create --driver macvlan -o 'parent=eth0' 'lan'",
            NetworkCommands.Create("lan", "macvlan", false, "docker", new NetworkIpam(null, null, null, false, null, "eth0")));
    }

    [Theory]
    [InlineData("172.30.0.0/33", null, null, false, null, null, "bridge")]          // no network
    [InlineData("fd00::/64", null, null, false, null, null, "bridge")]              // IPv6 in the IPv4 field
    [InlineData("172.30.0.0/24", "10.0.0.1", null, false, null, null, "bridge")]    // gateway outside
    [InlineData(null, "172.30.0.1", null, false, null, null, "bridge")]             // gateway without subnet
    [InlineData("172.30.0.0/24", null, "10.0.0.0/25", false, null, null, "bridge")] // range outside
    [InlineData("172.30.0.0/24", null, "172.30.0.0/16", false, null, null, "bridge")] // range wider than the subnet
    [InlineData(null, null, null, false, "fd00::/64", null, "bridge")]              // IPv6 subnet without IPv6
    [InlineData(null, null, null, false, null, "eth0", "bridge")]                    // parent only for macvlan/ipvlan
    [InlineData(null, null, null, false, null, "eth0; id", "macvlan")]
    public void Addresses_that_do_not_fit_are_refused(string? subnet, string? gateway, string? range, bool v6, string? v6Subnet, string? parent, string driver)
    {
        var ipam = new NetworkIpam(subnet, gateway, range, v6, v6Subnet, parent);

        Assert.NotNull(ipam.Problem(driver));
        Assert.Throws<ArgumentException>(() => NetworkCommands.Create("n", driver, false, "docker", ipam));
    }

    [Fact]
    public void Connect_takes_a_fixed_address_and_aliases_and_checks_them()
    {
        Assert.EndsWith("network connect --ip '172.30.0.9' --alias 'api' --alias 'web' 'app-net' 'web1'",
            NetworkCommands.Connect("app-net", "web1", "docker", "172.30.0.9", ["api", "web"]));
        Assert.EndsWith("network connect --ip6 'fd00::9' 'app-net' 'web1'", NetworkCommands.Connect("app-net", "web1", "docker", "fd00::9"));
        Assert.EndsWith("network connect 'app-net' 'web1'", NetworkCommands.Connect("app-net", "web1"));
        Assert.Throws<ArgumentException>(() => NetworkCommands.Connect("app-net", "web1", "docker", "not-an-ip"));
        Assert.Throws<ArgumentException>(() => NetworkCommands.Connect("app-net", "web1", "docker", null, ["bad alias"]));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_form_hands_the_addresses_to_docker()
    {
        var browser = await AdminAsync();
        var form = await (await browser.GetAsync("/Networks/Create")).Content.ReadAsStringAsync();
        Assert.Contains("Input.Subnet", form);

        var post = await browser.PostAsync("/Networks/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(form),
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Name"] = "app-net",
            ["Input.Driver"] = "bridge",
            ["Input.Subnet"] = "172.30.0.0/24",
            ["Input.Gateway"] = "172.30.0.1",
        }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal(("172.30.0.0/24", "172.30.0.1"), (_site.App.Connection.LastIpam?.Subnet, _site.App.Connection.LastIpam?.Gateway));
    }

    [Fact]
    public async Task The_connect_form_passes_the_fixed_address_and_the_aliases()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync($"/Networks/Detail?envId={_site.EnvId}&network=app-net")).Content.ReadAsStringAsync();

        await browser.PostAsync($"/Networks/Detail?envId={_site.EnvId}&network=app-net&handler=Connect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["envId"] = _site.EnvId.ToString(),
            ["network"] = "app-net",
            ["container"] = "web1",
            ["ip"] = "172.30.0.9",
            ["aliases"] = "api, web",
        }));

        var connect = _site.App.Connection.LastConnect;
        Assert.Equal(("app-net", "web1", "172.30.0.9"), (connect?.Network, connect?.Container, connect?.Ip));
        Assert.Equal(new[] { "api", "web" }, connect?.Aliases);
    }
}
