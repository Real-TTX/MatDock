using MatDock.Core.Docker;
using Xunit;

namespace MatDock.Tests;

public class DockerVolumeDetailTests
{
    [Fact]
    public void Cifs_volume_exposes_device_and_is_remote()
    {
        var d = new DockerVolumeDetail
        {
            Name = "share",
            Driver = "local",
            Options = new Dictionary<string, string>
            {
                ["type"] = "cifs",
                ["device"] = "//nas/backups",
                ["o"] = "addr=10.0.0.5,username=u,password=secret"
            }
        };

        Assert.True(d.IsRemote);
        Assert.Equal("//nas/backups", d.Device);
    }

    [Fact]
    public void Plain_local_volume_is_not_remote()
    {
        var d = new DockerVolumeDetail { Name = "data", Driver = "local" };
        Assert.False(d.IsRemote);
        Assert.Null(d.Device);
    }
}
