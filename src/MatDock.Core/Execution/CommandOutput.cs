namespace MatDock.Core.Execution;

/// <summary>
/// Ambient sink for live command output. While a sink is set for the current async flow (an API operation sets
/// one), <see cref="Stacks.StackService"/> streams the output of its host commands into it as it arrives instead
/// of only returning it at the end. Without a sink nothing changes (the UI keeps the buffered behaviour).
/// </summary>
public static class CommandOutput
{
    private static readonly AsyncLocal<Action<string>?> Sink = new();

    /// <summary>The sink of the current async flow, or null.</summary>
    public static Action<string>? Current => Sink.Value;

    /// <summary>Sets <paramref name="sink"/> for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable Redirect(Action<string> sink)
    {
        var previous = Sink.Value;
        Sink.Value = sink;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action<string>? _previous;

        public Scope(Action<string>? previous) => _previous = previous;

        public void Dispose() => Sink.Value = _previous;
    }
}
