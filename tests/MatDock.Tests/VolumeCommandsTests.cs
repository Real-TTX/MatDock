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
    public void CreateWithOptions_builds_nfs_command_with_driver_and_quoted_opts()
    {
        var cmd = VolumeCommands.CreateWithOptions("nfsvol", "local", new List<(string, string)>
        {
            ("type", "nfs"),
            ("o", "addr=10.0.0.5,rw,nfsvers=4"),
            ("device", ":/export/appdata"),
        });

        Assert.Contains("docker volume create --driver local", cmd);
        Assert.Contains("--opt type='nfs'", cmd);
        Assert.Contains("--opt o='addr=10.0.0.5,rw,nfsvers=4'", cmd);
        Assert.Contains("--opt device=':/export/appdata'", cmd);
        Assert.Contains(" 'nfsvol'", cmd);
    }

    [Fact]
    public void CreateWithOptions_quotes_values_and_rejects_bad_names_and_keys()
    {
        // A password with shell metacharacters must be safely single-quoted, not executed.
        var cmd = VolumeCommands.CreateWithOptions("v", "local", new List<(string, string)> { ("o", "password=a'b;$(x)") });
        Assert.Contains("--opt o='password=a'\\''b;$(x)'", cmd);

        Assert.Throws<ArgumentException>(() => VolumeCommands.CreateWithOptions("$(evil)", "local", new List<(string, string)>()));
        Assert.Throws<ArgumentException>(() => VolumeCommands.CreateWithOptions("v", "bad;driver", new List<(string, string)>()));
        Assert.Throws<ArgumentException>(() => VolumeCommands.CreateWithOptions("v", "local", new List<(string, string)> { ("bad key", "x") }));
    }

    [Fact]
    public void Builders_throw_on_injection_attempt()
    {
        Assert.Throws<ArgumentException>(() => VolumeCommands.Export("evil; rm -rf /", "busybox"));
        Assert.Throws<ArgumentException>(() => VolumeCommands.Import("data", "busybox; evil"));
        Assert.Throws<ArgumentException>(() => VolumeCommands.Create("$(evil)"));
    }

    [Theory]
    [InlineData(false, null, "docker")]
    [InlineData(true, null, "sudo -n docker")]
    [InlineData(false, "unix:///run/user/1000/docker.sock", "DOCKER_HOST=unix:///run/user/1000/docker.sock docker")]
    [InlineData(true, "unix:///run/user/1000/docker.sock", "sudo -n DOCKER_HOST=unix:///run/user/1000/docker.sock docker")]
    public void DockerHead_composes_expected(bool sudo, string? host, string expected)
        => Assert.Equal(expected, VolumeCommands.DockerHead(sudo, host));

    [Theory]
    [InlineData("unix:///run/user/1000/docker.sock", true)]
    [InlineData("tcp://10.0.0.5:2375", true)]
    [InlineData("unix:///run/docker.sock; rm -rf /", false)]
    [InlineData("$(evil)", false)]
    [InlineData("http://x", false)]
    public void IsValidDockerHost_validates(string host, bool expected)
        => Assert.Equal(expected, VolumeCommands.IsValidDockerHost(host));

    [Fact]
    public void DockerHead_throws_on_injection_host()
        => Assert.Throws<ArgumentException>(() => VolumeCommands.DockerHead(false, "unix:///x`id`"));

    [Fact]
    public void VolumeList_keeps_doubled_go_template_braces()
    {
        var cmd = VolumeCommands.VolumeList("docker");
        Assert.Contains("--format '{{json .}}'", cmd);   // must stay doubled for the remote shell
        Assert.DoesNotContain("'{json .}'", cmd);         // the collapsed form would break docker
    }

    [Fact]
    public void VolumeListDangling_lists_unused_volume_names_only()
    {
        var cmd = VolumeCommands.VolumeListDangling("docker");
        Assert.Contains("docker volume ls -q --filter dangling=true", cmd);
    }

    [Fact]
    public void Prune_removes_unused_volumes_non_interactively()
    {
        var cmd = VolumeCommands.Prune("docker");
        Assert.Contains("docker volume prune -f", cmd);
    }

    [Fact]
    public void ServerVersion_keeps_doubled_go_template_braces()
    {
        var cmd = VolumeCommands.ServerVersion("sudo -n docker");
        Assert.Contains("sudo -n docker version --format '{{json .Server}}'", cmd);
    }

    [Fact]
    public void Import_with_clearFirst_wipes_target_before_extract()
    {
        var cmd = VolumeCommands.Import("data", "busybox", "docker", clearFirst: true);
        Assert.Contains("rm -rf /to/*", cmd);
        Assert.Contains("exec tar -C /to -xf -", cmd);
    }

    [Fact]
    public void Import_without_clearFirst_does_not_wipe()
    {
        var cmd = VolumeCommands.Import("data", "busybox");
        Assert.DoesNotContain("rm -rf", cmd);
    }

    [Fact]
    public void Inspect_builds_expected_and_validates()
    {
        Assert.Contains("docker volume inspect 'data'", VolumeCommands.Inspect("data"));
        Assert.Throws<ArgumentException>(() => VolumeCommands.Inspect("$(evil)"));
    }
}
