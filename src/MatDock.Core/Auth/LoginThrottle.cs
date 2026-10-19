using System.Collections.Concurrent;

namespace MatDock.Core.Auth;

/// <summary>
/// Slows down password guessing on the local sign-in: after <see cref="MaxFailuresPerAccount"/> failed attempts for one
/// account from one address, or <see cref="MaxFailuresPerAddress"/> from one address in total, within <see cref="Window"/>,
/// further attempts are refused until the oldest failure has left the window. In memory (a restart forgets it).
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailuresPerAccount = 5;
    public const int MaxFailuresPerAddress = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public LoginThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>How long the next attempt has to wait; null = it may go ahead.</summary>
    public TimeSpan? RetryAfter(string? address, string? username)
    {
        var now = _time.GetUtcNow();
        var wait = Wait(AccountKey(address, username), MaxFailuresPerAccount, now);
        var waitAddress = Wait(AddressKey(address), MaxFailuresPerAddress, now);
        return wait is null ? waitAddress : waitAddress is null ? wait : (wait > waitAddress ? wait : waitAddress);
    }

    public void Failed(string? address, string? username)
    {
        var now = _time.GetUtcNow();
        Add(AccountKey(address, username), now);
        Add(AddressKey(address), now);
        if (_failures.Count > 10_000)
        {
            Sweep(now);
        }
    }

    /// <summary>A successful sign-in forgets the failures of that account (not those of the address).</summary>
    public void Succeeded(string? address, string? username) => _failures.TryRemove(AccountKey(address, username), out _);

    private TimeSpan? Wait(string key, int max, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var queue))
        {
            return null;
        }

        lock (queue)
        {
            Trim(queue, now);
            return queue.Count >= max ? queue.Peek() + Window - now : null;
        }
    }

    private void Add(string key, DateTimeOffset now)
    {
        var queue = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Trim(queue, now);
            queue.Enqueue(now);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, queue) in _failures)
        {
            lock (queue)
            {
                Trim(queue, now);
                if (queue.Count == 0)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }

    private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && queue.Peek() + Window <= now)
        {
            queue.Dequeue();
        }
    }

    private static string AccountKey(string? address, string? username)
        => "u|" + (address ?? "-") + "|" + (username ?? string.Empty).Trim().ToLowerInvariant();

    private static string AddressKey(string? address) => "a|" + (address ?? "-");
}
