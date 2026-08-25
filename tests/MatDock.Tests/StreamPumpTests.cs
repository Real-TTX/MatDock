using System.Text;
using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class StreamPumpTests
{
    [Fact]
    public async Task Copies_all_bytes_and_returns_count()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 200_000)); // larger than the 80 KiB buffer
        using var from = new MemoryStream(payload);
        using var to = new MemoryStream();

        var count = await StreamPump.CopyAsync(from, to);

        Assert.Equal(payload.Length, count);
        Assert.Equal(payload, to.ToArray());
    }

    [Fact]
    public async Task Empty_source_returns_zero()
    {
        using var from = new MemoryStream(Array.Empty<byte>());
        using var to = new MemoryStream();

        Assert.Equal(0, await StreamPump.CopyAsync(from, to));
    }
}
