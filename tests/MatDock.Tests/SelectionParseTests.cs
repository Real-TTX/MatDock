using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class SelectionParseTests
{
    [Fact]
    public void Parses_env_and_volume_pairs()
    {
        var result = VolumeSelection.Parse(new[] { "5|app-data", "7|db", "bad", "|x", "9|" }).ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal((5L, "app-data"), result[0]);
        Assert.Equal((7L, "db"), result[1]);
    }

    [Fact]
    public void Keeps_pipe_in_volume_name_after_first_separator()
    {
        var result = VolumeSelection.Parse(new[] { "3|weird|name" }).Single();
        Assert.Equal((3L, "weird|name"), result);
    }
}
