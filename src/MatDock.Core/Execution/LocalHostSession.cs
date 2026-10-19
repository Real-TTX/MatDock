using System.Diagnostics;
using MatDock.Core.Ssh;

namespace MatDock.Core.Execution;

/// <summary>
/// Local <see cref="IHostSession"/>: runs the same Docker CLI command strings on the MatDock host itself
/// (via <c>/bin/sh -c</c>), talking to the mounted Docker socket. Intended for the Linux container
/// deployment with <c>-v /var/run/docker.sock:/var/run/docker.sock</c> and the docker CLI in the image.
/// </summary>
internal sealed class LocalHostSession : IHostSession
{
    private readonly string? _stacksPath;
    private readonly IReadOnlyDictionary<string, string>? _docker;
    private readonly string? _certDir;

    /// <param name="stacksPath">Base directory for the stack files (exported as <c>MATDOCK_STACKS_DIR</c> to the
    /// commands), or null for the default below the home directory.</param>
    /// <param name="docker">DOCKER_* variables of the commands (a remote Docker API), or null for the mounted socket.</param>
    /// <param name="certDir">A folder of TLS certificates that the session owns and removes at the end.</param>
    public LocalHostSession(string? stacksPath = null, IReadOnlyDictionary<string, string>? docker = null, string? certDir = null)
    {
        _stacksPath = stacksPath;
        _docker = docker;
        _certDir = certDir;
    }

    /// <summary>
    /// A session whose commands talk to the Docker API of <paramref name="settings"/> (tcp://Host:Port). With TLS the CA, the client
    /// certificate and its key are written to a private temporary folder for <c>DOCKER_CERT_PATH</c>, removed with the session.
    /// </summary>
    public static LocalHostSession ForDockerApi(string? stacksPath, SshConnectionSettings settings)
    {
        var host = settings.Host.Contains(':') && !settings.Host.StartsWith('[') ? $"[{settings.Host}]" : settings.Host;
        var docker = new Dictionary<string, string>(StringComparer.Ordinal) { ["DOCKER_HOST"] = $"tcp://{host}:{settings.Port}" };
        string? certDir = null;
        if (!string.IsNullOrWhiteSpace(settings.TlsCaPem) || !string.IsNullOrWhiteSpace(settings.TlsCertPem))
        {
            certDir = Directory.CreateTempSubdirectory("matdock-tls-").FullName;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(certDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.WriteAllText(Path.Combine(certDir, "ca.pem"), settings.TlsCaPem ?? string.Empty);
            File.WriteAllText(Path.Combine(certDir, "cert.pem"), settings.TlsCertPem ?? string.Empty);
            File.WriteAllText(Path.Combine(certDir, "key.pem"), settings.TlsKeyPem ?? string.Empty);
            docker["DOCKER_TLS_VERIFY"] = "1";
            docker["DOCKER_CERT_PATH"] = certDir;
        }

        return new LocalHostSession(stacksPath, docker, certDir);
    }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask; // nothing to connect; the CLI connects per command

    public IHostCommand CreateCommand(string command) => new LocalHostCommand(command, _stacksPath, _docker);

    public void Dispose()
    {
        if (_certDir is not null)
        {
            try { Directory.Delete(_certDir, recursive: true); } catch { /* best effort */ }
        }
    }
}

internal sealed class LocalHostCommand : IHostCommand
{
    private readonly string _command;
    private readonly string? _stacksPath;
    private readonly IReadOnlyDictionary<string, string>? _docker;
    private Process? _process;
    private Task<string>? _stdoutTask;
    private Task<string>? _stderrTask;
    private bool _stdinCreated;

    public LocalHostCommand(string command, string? stacksPath = null, IReadOnlyDictionary<string, string>? docker = null)
    {
        _command = command;
        _stacksPath = stacksPath;
        _docker = docker;
    }

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int? ExitStatus { get; private set; }

    public string Error { get; private set; } = string.Empty;

    public string Result { get; private set; } = string.Empty;

    public bool BufferOutput { get; set; } = true;

    public Stream OutputStream => BufferOutput
        ? throw new InvalidOperationException("OutputStream requires BufferOutput=false (stdout is buffered into Result).")
        : (_process ?? throw new InvalidOperationException("Command not started.")).StandardOutput.BaseStream;

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
        KeepOnlySafeVariables(psi.Environment);
        if (_docker is not null)
        {
            // A remote Docker API: these win over the DOCKER_* of MatDock's own process.
            foreach (var name in psi.Environment.Keys.Where(k => k.StartsWith("DOCKER_", StringComparison.Ordinal)).ToList())
            {
                psi.Environment.Remove(name);
            }

            foreach (var (name, value) in _docker)
            {
                psi.Environment[name] = value;
            }
        }

        if (!string.IsNullOrEmpty(_stacksPath))
        {
            psi.Environment[Stacks.StackCommands.StacksDirVariable] = _stacksPath;
        }

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

    /// <summary>
    /// The commands get only what a shell and the Docker CLI need, never MatDock's own settings: `docker compose` prefers a variable of its process
    /// over the `.env` of a stack, so MatDock's MAT_API_KEY, MATIDENTITY_* or connection strings would otherwise end up in every stack it deploys.
    /// </summary>
    internal static void KeepOnlySafeVariables(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.ToList())
        {
            if (!IsSafeVariable(name))
            {
                environment.Remove(name);
            }
        }
    }

    internal static bool IsSafeVariable(string name)
        => name is "PATH" or "HOME" or "USER" or "LOGNAME" or "SHELL" or "LANG" or "TZ" or "TMPDIR" or "TERM"
           || name.StartsWith("LC_", StringComparison.Ordinal)
           || name.StartsWith("DOCKER_", StringComparison.Ordinal)
           || name.StartsWith("SSL_CERT_", StringComparison.Ordinal);

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

        // Bound the WHOLE wait to a single timeout budget: wait for exit first, then give the drain tasks a
        // short grace (they finish once the pipes hit EOF at exit). A wedged process is killed at the deadline.
        bool exited;
        try { exited = _process.WaitForExit(timeoutMs); }
        catch { exited = false; }

        if (!exited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        const int GraceMs = 5000;
        if (BufferOutput && _stdoutTask is not null)
        {
            Result = WaitFor(_stdoutTask, GraceMs);
        }
        Error = _stderrTask is not null ? WaitFor(_stderrTask, GraceMs) : string.Empty;

        try { ExitStatus = exited ? _process.ExitCode : -1; }
        catch { ExitStatus = -1; }
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

        // Observe the background drain tasks so a fault (e.g. ObjectDisposedException from killing mid-read
        // after a pre-EndExecute exception) doesn't surface as an UnobservedTaskException.
        Observe(_stdoutTask);
        Observe(_stderrTask);

        _process?.Dispose();
    }

    private static void Observe(Task<string>? task)
    {
        if (task is null)
        {
            return;
        }

        try { task.Wait(500); } catch { /* observed */ }
        _ = task.Exception; // mark observed even if it didn't finish within the grace
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
