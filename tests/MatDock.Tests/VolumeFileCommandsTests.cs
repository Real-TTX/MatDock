using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class VolumeFileCommandsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("a/b", "a/b")]
    [InlineData("/a//b/", "a/b")]
    [InlineData("a/./b", "a/b")]
    [InlineData("conf\\app", "conf/app")]
    public void NormalizeRelPath_normalizes_safe_paths(string? input, string expected)
        => Assert.Equal(expected, VolumeFileCommands.NormalizeRelPath(input));

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b/..")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void NormalizeRelPath_rejects_traversal_and_control_chars(string input)
        => Assert.Null(VolumeFileCommands.NormalizeRelPath(input));

    [Fact]
    public void ContainerPath_stays_under_data()
    {
        Assert.Equal("/data", VolumeFileCommands.ContainerPath(""));
        Assert.Equal("/data/a/b", VolumeFileCommands.ContainerPath("a/b"));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("with space", "'with space'")]
    [InlineData("O'Brien", "'O'\\''Brien'")]
    [InlineData("a;rm -rf /", "'a;rm -rf /'")]
    public void ShellQuote_escapes_single_quotes_and_wraps(string input, string expected)
        => Assert.Equal(expected, VolumeFileCommands.ShellQuote(input));

    [Fact]
    public void List_command_quotes_volume_and_path()
    {
        var cmd = VolumeFileCommands.List("docker", "busybox", "myvol", "sub dir");
        Assert.Contains("-v 'myvol':/data:ro", cmd);
        Assert.Contains("'/data/sub dir'", cmd);
    }

    [Fact]
    public void Delete_command_targets_quoted_container_path()
    {
        var cmd = VolumeFileCommands.Delete("docker", "busybox", "v", "a/b");
        Assert.Contains("rm -rf -- '/data/a/b'", cmd);
    }
}
