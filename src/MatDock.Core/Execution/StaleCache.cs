using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Execution;

/// <summary>
/// A reading of a host that a page shows but need not wait for (docker stats, host load, events): within <c>fresh</c> it is used as it is; older,
/// it is still shown while one new reading is taken in the background; with none at all the page waits for the first one at most <c>wait</c>. So a
/// slow host (a busy Docker answers <c>docker stats</c> in seconds) no longer holds up every page after the cache ran out, and several pages and the
/// samplers asking at once share one reading.
/// </summary>
public static class StaleCache
{
    /// <summary>How long a reading of a background load may take (it does not end with the page that asked).</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, Task> Loading = new(StringComparer.Ordinal);

    private sealed record Entry<T>(T Value, DateTime AtUtc);

    /// <summary>
    /// The value of <paramref name="key"/>: fresh, or stale with a new reading started, or the first reading if it comes within <paramref name="wait"/>;
    /// otherwise <c>default</c> (the reading goes on and is there next time). <paramref name="load"/> must not use anything of the request (it outlives it).
    /// </summary>
    public static async Task<T?> GetAsync<T>(IMemoryCache cache, string key, TimeSpan fresh, TimeSpan keep, TimeSpan wait,
        Func<CancellationToken, Task<T>> load, ILogger? logger = null, CancellationToken ct = default)
    {
        if (cache.TryGetValue(key, out Entry<T>? entry) && entry is not null)
        {
            if (DateTime.UtcNow - entry.AtUtc >= fresh)
            {
                _ = Start(cache, key, keep, load, logger);
            }

            return entry.Value;
        }

        var task = Start(cache, key, keep, load, logger);
        try
        {
            await task.WaitAsync(wait, ct);
        }
        catch (TimeoutException)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return default; // a failed reading: nothing to show, the next ask tries again
        }

        return cache.TryGetValue(key, out entry) && entry is not null ? entry.Value : default;
    }

    /// <summary>Forgets <paramref name="key"/> (after a change that the next page must see).</summary>
    public static void Forget(IMemoryCache cache, string key) => cache.Remove(key);

    /// <summary>One reading per key at a time: a second ask while one runs gets the same.</summary>
    private static Task Start<T>(IMemoryCache cache, string key, TimeSpan keep, Func<CancellationToken, Task<T>> load, ILogger? logger)
    {
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Loading.GetOrAdd(key, mine.Task);
        if (running != mine.Task)
        {
            return running;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(LoadTimeout);
                var value = await load(timeout.Token);
                cache.Set(key, new Entry<T>(value, DateTime.UtcNow), keep);
                Loading.TryRemove(new KeyValuePair<string, Task>(key, mine.Task));
                mine.SetResult();
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Reading {Key} failed.", key);
                Loading.TryRemove(new KeyValuePair<string, Task>(key, mine.Task));
                mine.SetException(ex);
                _ = mine.Task.Exception; // observed: a failed background reading is not an error of anyone
            }
        });

        return mine.Task;
    }
}
