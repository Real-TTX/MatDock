namespace MatDock.Core.Volumes;

/// <summary>Copies one stream into another, returning the number of bytes transferred.</summary>
public static class StreamPump
{
    public static async Task<long> CopyAsync(Stream from, Stream to, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        await to.FlushAsync(cancellationToken);
        return total;
    }
}
