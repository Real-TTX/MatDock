namespace MatDock.Core.Execution;

/// <summary>
/// Counts what MatDock itself may have changed on the hosts: every request that is not a read (a click on Stop, a deploy through the API) and every
/// background operation that ended. A list that is kept for a moment (the containers of the dashboard and the stacks, 30 s) carries the count in its
/// key, so right after a change it is read again and does not show the state from before: a stack that was just stopped stood there as running.
/// </summary>
public static class HostChanges
{
    private static long _count;

    /// <summary>Something may have changed: what was kept before is not used again.</summary>
    public static void Note() => Interlocked.Increment(ref _count);

    /// <summary>The cache key of a kept reading of one environment, of the state after the last change.</summary>
    public static string Key(string what, long environmentId) => $"{what}:{Interlocked.Read(ref _count)}:{environmentId}";
}
