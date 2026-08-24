using Renci.SshNet;

namespace MatDock.Core.Docker;

/// <summary>
/// A single raw, bidirectional byte pipe to a remote Docker daemon socket, obtained by executing
/// <c>docker system dial-stdio</c> over SSH — the very mechanism the Docker CLI uses for
/// <c>DOCKER_HOST=ssh://…</c>. Bytes written to <see cref="Input"/> reach the daemon socket's stdin;
/// bytes read from <see cref="Output"/> are the daemon socket's stdout.
/// </summary>
internal sealed class SshDialStdioChannel : IDisposable
{
    private const string DialCommand = "docker system dial-stdio";

    private readonly SshCommand _command;
    private readonly IAsyncResult _execution;
    private int _disposed;

    private SshDialStdioChannel(SshCommand command, Stream input, IAsyncResult execution)
    {
        _command = command;
        Input = input;
        _execution = execution;
    }

    /// <summary>Writable stream = remote command stdin.</summary>
    public Stream Input { get; }

    /// <summary>Readable stream = remote command stdout.</summary>
    public Stream Output => _command.OutputStream;

    public static SshDialStdioChannel Open(SshClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        SshCommand? command = null;
        Stream? input = null;
        try
        {
            command = client.CreateCommand(DialCommand);
            input = command.CreateInputStream();
            var execution = command.BeginExecute();
            return new SshDialStdioChannel(command, input, execution);
        }
        catch
        {
            input?.Dispose();
            command?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { Input.Dispose(); } catch { /* channel already torn down */ }
        try { _command.EndExecute(_execution); } catch { /* command aborted */ }
        try { _command.Dispose(); } catch { /* best effort */ }
    }
}
