using Renci.SshNet;

namespace MatDock.Core.Execution;

/// <summary>SSH-backed <see cref="IHostSession"/>: a thin wrapper over an SSH.NET <see cref="SshClient"/>.</summary>
internal sealed class SshHostSession : IHostSession
{
    private readonly SshClient _client;

    public SshHostSession(SshClient client) => _client = client;

    public Task ConnectAsync(CancellationToken ct) => _client.ConnectAsync(ct);

    public IHostCommand CreateCommand(string command) => new SshHostCommand(_client.CreateCommand(command));

    public void Dispose() => _client.Dispose();
}

internal sealed class SshHostCommand : IHostCommand
{
    private readonly SshCommand _cmd;

    public SshHostCommand(SshCommand cmd) => _cmd = cmd;

    public TimeSpan CommandTimeout { get => _cmd.CommandTimeout; set => _cmd.CommandTimeout = value; }

    public int? ExitStatus => _cmd.ExitStatus;

    public string Error => _cmd.Error ?? string.Empty;

    public string Result => _cmd.Result ?? string.Empty;

    public Stream OutputStream => _cmd.OutputStream;

    // SSH.NET buffers channel data via the session message pump, so Result works regardless; no-op here.
    public bool BufferOutput { get; set; } = true;

    public string Execute() => _cmd.Execute() ?? string.Empty;

    public Task ExecuteAsync(CancellationToken ct) => _cmd.ExecuteAsync(ct);

    public IAsyncResult BeginExecute() => _cmd.BeginExecute();

    public Stream CreateInputStream() => _cmd.CreateInputStream();

    public void EndExecute(IAsyncResult asyncResult) => _cmd.EndExecute(asyncResult);

    public void Dispose() => _cmd.Dispose();
}
