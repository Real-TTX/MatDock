using MatDock.Core.Proxies;
using Xunit;

namespace MatDock.Tests;

public class CaddyProxyTests
{
    [Theory]
    [InlineData("app.example.com", "app-example-com")]
    [InlineData("APP", "app")]
    [InlineData(".x.", "x")]
    [InlineData("a/b:c", "a-b-c")]
    public void Slug_is_safe_and_stable(string host, string expected)
        => Assert.Equal(expected, CaddyProxyProvider.Slug(host));

    [Fact]
    public void Slug_falls_back_when_empty()
        => Assert.Equal("route", CaddyProxyProvider.Slug("---"));
}
