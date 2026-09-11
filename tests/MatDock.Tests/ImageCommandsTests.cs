using MatDock.Core.Images;
using Xunit;

namespace MatDock.Tests;

public class ImageCommandsTests
{
    [Theory]
    [InlineData("nginx:latest")]
    [InlineData("ghcr.io/org/app:1.2.3")]
    [InlineData("registry.example.com:5000/team/app:tag")]
    [InlineData("sha256:abc123")]
    [InlineData("a1b2c3d4e5f6")]
    [InlineData("repo@sha256:deadbeef")]
    public void Accepts_valid_image_refs(string reference)
        => Assert.True(ImageCommands.IsValidImageRef(reference));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("evil;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("a`id`")]
    [InlineData("a|b")]
    [InlineData("-leading")]
    [InlineData("with\nnewline")]
    public void Rejects_invalid_or_injection_refs(string reference)
        => Assert.False(ImageCommands.IsValidImageRef(reference));

    [Fact]
    public void List_builds_json_format_command()
    {
        var cmd = ImageCommands.List("docker");
        Assert.Contains("docker image ls --digests --format '{{json .}}'", cmd);
    }

    [Fact]
    public void Remove_quotes_reference_and_omits_force_by_default()
    {
        var cmd = ImageCommands.Remove("nginx:latest", force: false, "sudo -n docker");
        Assert.Contains("sudo -n docker image rm 'nginx:latest'", cmd);
        Assert.DoesNotContain(" -f ", cmd);
    }

    [Fact]
    public void Remove_adds_force_flag_when_requested()
    {
        var cmd = ImageCommands.Remove("nginx:latest", force: true, "docker");
        Assert.Contains("docker image rm -f 'nginx:latest'", cmd);
    }

    [Fact]
    public void Remove_throws_on_injection_reference()
        => Assert.Throws<System.ArgumentException>(() => ImageCommands.Remove("evil; rm -rf /", force: false, "docker"));

    [Fact]
    public void Prune_dangling_by_default_and_all_when_requested()
    {
        Assert.Contains("docker image prune -f", ImageCommands.Prune(all: false, "docker"));
        Assert.DoesNotContain("-a", ImageCommands.Prune(all: false, "docker"));
        Assert.Contains("docker image prune -f -a", ImageCommands.Prune(all: true, "docker"));
    }
}
