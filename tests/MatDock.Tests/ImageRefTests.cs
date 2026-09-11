using MatDock.Core.Updates;
using Xunit;

namespace MatDock.Tests;

public class ImageRefTests
{
    [Theory]
    [InlineData("nginx", "docker.io", "library/nginx", "latest")]
    [InlineData("nginx:1.27", "docker.io", "library/nginx", "1.27")]
    [InlineData("org/app", "docker.io", "org/app", "latest")]
    [InlineData("ghcr.io/org/app:1.2", "ghcr.io", "org/app", "1.2")]
    [InlineData("registry.example.com:5000/team/app", "registry.example.com:5000", "team/app", "latest")]
    [InlineData("localhost:5000/x:dev", "localhost:5000", "x", "dev")]
    public void Parses_host_repo_tag(string image, string host, string repo, string tag)
    {
        var r = ImageRef.TryParse(image);
        Assert.NotNull(r);
        Assert.Equal(host, r!.Host);
        Assert.Equal(repo, r.Repository);
        Assert.Equal(tag, r.Tag);
        Assert.False(r.IsDigestPinned);
    }

    [Fact]
    public void Recognizes_digest_pin()
    {
        var r = ImageRef.TryParse("ghcr.io/org/app@sha256:abc123");
        Assert.NotNull(r);
        Assert.True(r!.IsDigestPinned);
        Assert.Equal("sha256:abc123", r.Digest);
        Assert.Equal("org/app", r.Repository);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Returns_null_for_empty(string? image)
        => Assert.Null(ImageRef.TryParse(image));
}
