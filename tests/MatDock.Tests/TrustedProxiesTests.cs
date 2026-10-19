using System.Net;
using MatDock.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace MatDock.Tests;

public class TrustedProxiesTests
{
    [Fact]
    public void Unset_every_sender_is_believed_as_before()
    {
        var options = new ForwardedHeadersOptions();
        Assert.Null(TrustedProxies.Apply(options, null));
        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void Set_only_the_listed_proxies_and_the_loopback_are_believed()
    {
        var options = new ForwardedHeadersOptions();
        Assert.Null(TrustedProxies.Apply(options, " 172.18.0.0/16, 10.0.0.5 ,fd00::/8"));
        Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("172.18.3.4")));
        Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Loopback));
        Assert.Contains(IPAddress.Parse("10.0.0.5"), options.KnownProxies);
        Assert.DoesNotContain(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("192.168.1.10")));
    }

    [Fact]
    public void A_wrong_entry_is_named()
        => Assert.Contains("'proxy.local'", TrustedProxies.Apply(new ForwardedHeadersOptions(), "10.0.0.0/8, proxy.local"));
}
