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
    [InlineData("C:/Windows/win.ini")]   // Windows drive letter would root a local Path.Combine
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("file:stream")]           // ':' (drive / alternate-data-stream) is always rejected
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

public class ContainerFileCommandsTests
{
    private static readonly MatDock.Core.Volumes.FileTarget Web = MatDock.Core.Volumes.FileTarget.ForContainer("web-1");

    [Fact]
    public void A_container_runs_the_commands_itself_below_its_root()
    {
        var list = MatDock.Core.Volumes.VolumeFileCommands.List("docker", "busybox", Web, "etc/nginx");
        Assert.Contains("docker exec 'web-1' sh -c ", list);
        Assert.EndsWith(" sh '/etc/nginx'", list);
        Assert.DoesNotContain("busybox", list);

        Assert.EndsWith(" sh '/'", MatDock.Core.Volumes.VolumeFileCommands.List("docker", "busybox", Web, ""));
        Assert.Contains("docker exec -i 'web-1' sh -c ", MatDock.Core.Volumes.VolumeFileCommands.Write("docker", "busybox", Web, "app/x.conf"));
        Assert.EndsWith("docker exec 'web-1' rm -rf -- '/tmp/a b'", MatDock.Core.Volumes.VolumeFileCommands.Delete("docker", "busybox", Web, "tmp/a b"));
    }

    [Fact]
    public void A_download_reads_the_whole_file_and_the_editor_only_the_beginning()
    {
        var volume = MatDock.Core.Volumes.FileTarget.ForVolume("data");
        Assert.Contains("cat -- ", MatDock.Core.Volumes.VolumeFileCommands.ReadAll("docker", "busybox", volume, "big.bin"));
        Assert.DoesNotContain("head -c", MatDock.Core.Volumes.VolumeFileCommands.ReadAll("docker", "busybox", volume, "big.bin"));
        Assert.Contains("head -c ", MatDock.Core.Volumes.VolumeFileCommands.Read("docker", "busybox", volume, "big.bin"));
    }

    [Theory]
    [InlineData(true, "web-1", true)]
    [InlineData(true, "x; reboot", false)]
    [InlineData(false, "pgdata", true)]
    [InlineData(false, "../etc", false)]
    public void Targets_are_validated(bool container, string name, bool valid)
        => Assert.Equal(valid, (container ? MatDock.Core.Volumes.FileTarget.ForContainer(name) : MatDock.Core.Volumes.FileTarget.ForVolume(name)).IsValid);
}
