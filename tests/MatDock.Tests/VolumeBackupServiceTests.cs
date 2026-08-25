using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class VolumeBackupServiceTests
{
    [Fact]
    public void BuildFileName_is_deterministic_and_safe()
    {
        var ts = new DateTime(2026, 8, 25, 14, 5, 9, DateTimeKind.Utc);
        Assert.Equal("7_app-data_20260825140509.tar", VolumeBackupService.BuildFileName(7, "app-data", ts));
    }
}
