using System.Diagnostics;

namespace MatDock.Core.Execution;

/// <summary>
/// Local <see cref="IHostSession"/>: runs the same Docker CLI command strings on the MatDock host itself
/// (via <c>/bin/sh -c</c>), talking to the mounted Docker socket. Intended for the Linux container
/// deployment with <c>-v /var/run/docker.sock:/var/run/docker.sock</c> and the docker CLI in the image.
/// </summary>
internal sealed class LocalHostSession : IHostSession
{
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask; // nothing to connect; the socket is local

    public IHostCommand CreateCommand(string command) => new LocalHostCommand(command);

    public void Dispose() { }
}

internal sealed class LocalHostCommand : IHostCommand
{
    private readonly string _command;
    private Process? _process;
    private Task<string>? _stdoutTask;
    private Task<string>? _stderrTask;
    private bool _stdinCreated;

    public LocalHostCommand(string command) => _command = command;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int? ExitStatus { get; private set; }

    public string Error { get; private set; } = string.Empty;

    public string Result { get; private set; } = string.Empty;

    public bool BufferOutput { get; set; } = true;

    public Stream OutputStream => (_process ?? throw new InvalidOperationException("Command not started.")).StandardOutput.BaseStream;

    public string Execute()
    {
        BufferOutput = true;
        EndExecute(BeginExecute());
        return Result;
    }

    public Task ExecuteAsync(CancellationToken ct) => Task.Run(() =>
    {
        BufferOutput = true;
        EndExecute(BeginExecute());
    }, ct);

    public IAsyncResult BeginExecute()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_command);

        _process = new Process { StartInfo = psi };
        _process.Start();

        // Always drain stderr so it can't fill the pipe and block the process.
        _stderrTask = Task.Run(() => _process!.StandardError.ReadToEnd());
        // Buffered mode: drain stdout too so Result works without the caller reading OutputStream.
        // Streaming mode (migration): leave stdout for the caller to pipe live.
        if (BufferOutput)
        {
            _stdoutTask = Task.Run(() => _process!.StandardOutput.ReadToEnd());
        }

        return CompletedAsyncResult.Instance;
    }

    public Stream CreateInputStream()
    {
        _stdinCreated = true;
        return (_process ?? throw new InvalidOperationException("Command not started.")).StandardInput.BaseStream;
    }

    public void EndExecute(IAsyncResult asyncResult)
    {
        if (_process is null)
        {
            return;
        }

        // If the caller never wrote stdin, close it so a command that reads stdin gets EOF instead of hanging.
        if (!_stdinCreated)
        {
            try { _process.StandardInput.Close(); } catch { /* already closed */ }
        }

        var timeoutMs = CommandTimeout <= TimeSpan.Zero ? -1 : (int)Math.Min(int.MaxValue, CommandTimeout.TotalMilliseconds);

        if (BufferOutput && _stdoutTask is not null)
        {
            Result = WaitFor(_stdoutTask, timeoutMs);
        }
        Error = _stderrTask is not null ? WaitFor(_stderrTask, timeoutMs) : string.Empty;

        try
        {
            if (_process.WaitForExit(timeoutMs))
            {
                ExitStatus = _process.ExitCode;
            }
            else
            {
                try { _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                ExitStatus = -1;
            }
        }
        catch
        {
            ExitStatus = -1;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }
        _process?.Dispose();
    }

    private static string WaitFor(Task<string> task, int timeoutMs)
    {
        try { return task.Wait(timeoutMs) ? task.Result : string.Empty; }
        catch { return string.Empty; }
    }

    private sealed class CompletedAsyncResult : IAsyncResult
    {
        public static readonly CompletedAsyncResult Instance = new();
        public bool IsCompleted => true;
        public WaitHandle AsyncWaitHandle => throw new NotSupportedException();
        public object? AsyncState => null;
        public bool CompletedSynchronously => true;
    }
}
