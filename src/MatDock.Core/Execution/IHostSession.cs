using MatDock.Core.Ssh;

namespace MatDock.Core.Execution;

/// <summary>
/// Abstraction over "run a Docker CLI command against a host". Two implementations: SSH (remote host)
/// and Local (the MatDock host's own Docker socket). Every Docker operation in MatDock is a POSIX shell
/// command string, so both implementations share the exact same command builders — only the transport
/// differs. The interface mirrors the subset of the SSH.NET API the services already use, so the SSH
/// implementation is a thin wrapper and call sites stay almost identical.
/// </summary>
public interface IHostSessionFactory
{
    IHostSession Create(SshConnectionSettings settings);
}

public interface IHostSession : IDisposable
{
    Task ConnectAsync(CancellationToken ct);

    IHostCommand CreateCommand(string command);
}

public interface IHostCommand : IDisposable
{
    TimeSpan CommandTimeout { get; set; }

    int? ExitStatus { get; }

    string Error { get; }

    string Result { get; }

    /// <summary>Raw stdout stream (used for piping large output, e.g. a tar stream during migration).</summary>
    Stream OutputStream { get; }

    /// <summary>
    /// When true (default) stdout is buffered so <see cref="Result"/> works even if the caller never reads
    /// <see cref="OutputStream"/>. Set to false BEFORE <see cref="BeginExecute"/> to pipe stdout live
    /// (volume migration) without buffering the whole archive in memory. Ignored by the SSH implementation.
    /// </summary>
    bool BufferOutput { get; set; }

    /// <summary>Runs synchronously to completion and returns stdout.</summary>
    string Execute();

    /// <summary>Runs to completion asynchronously; afterwards <see cref="Result"/>/<see cref="Error"/>/<see cref="ExitStatus"/> are set.</summary>
    Task ExecuteAsync(CancellationToken ct);

    IAsyncResult BeginExecute();

    Stream CreateInputStream();

    void EndExecute(IAsyncResult asyncResult);
}
