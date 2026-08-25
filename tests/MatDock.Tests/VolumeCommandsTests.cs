using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class VolumeCommandsTests
{
    [Theory]
    [InlineData("my-volume")]
    [InlineData("data_01")]
    [InlineData("app.data")]
    [InlineData("A1")]
    public void Accepts_valid_volume_names(string name)
        => Assert.True(VolumeCommands.IsValidVolumeName(name));

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("vol;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("a`id`")]
    [InlineData("a|b")]
    [InlineData("a&&b")]
    [InlineData("a/b")]
    public void Rejects_invalid_or_injection_names(string name)
        => Assert.False(VolumeCommands.IsValidVolumeName(name));

    [Fact]
    public void Export_builds_expected_command()
    {
        var cmd = VolumeCommands.Export("data", "busybox");
        Assert.Contains("docker run --rm -v 'data':/from:ro 'busybox' tar -C /from -cf - .", cmd);
    }

    [Fact]
    public void Import_builds_expected_command()
    {
        var cmd = VolumeCommands.Import("data", "busybox");
        Assert.Contains("docker run --rm -i -v 'data':/to 'busybox' tar -C /to -xf -", cmd);
    }

    [Fact]
    public void Create_builds_expected_command()
        => Assert.Contains("docker volume create 'data'", VolumeCommands.Create("data"));

    [Fact]
    public void Builders_throw_on_injection_attempt()
    {
        Assert.Throws<ArgumentException>(() => VolumeCommands.Export("evil; rm -rf /", "busybox"));
        Assert.Throws<ArgumentException>(() => VolumeCommands.Import("data", "busybox; evil"));
        Assert.Throws<ArgumentException>(() => VolumeCommands.Create("$(evil)"));
    }
}
