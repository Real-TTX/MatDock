using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MatDock.Core.Backups;

/// <summary>A MatDock snapshot found in a restic repository: the backup file it holds, when it was made and how big it was.</summary>
public sealed record ResticSnapshot(string Id, string FileName, DateTime? TimeUtc, long Bytes);

/// <summary>
/// Backup files as restic snapshots: every file MatDock writes becomes one snapshot of a single stdin "file" of that name, tagged
/// <c>mdfile:&lt;name&gt;</c> and with the host name <c>matdock</c>, so listing, reading and deleting a file are lookups by tag and other
/// snapshots in the same repository are never touched. restic encrypts, deduplicates and talks to S3, B2, Azure, GCS, a REST server or a path.
/// <para>
/// restic runs as a child process <b>without a shell</b> (an argument list), with an empty environment plus the repository, its password
/// and the backend's access keys — never on the command line, where <c>ps</c> would show them — and none of MatDock's own variables.
/// </para>
/// </summary>
public sealed class ResticBackupStorage : IBackupStorage
{
    // What the child process may inherit: where to find programs and temp files, the proxy and CA settings. Nothing of MatDock's own.
    private static readonly string[] PassThrough =
    [
        "PATH", "SystemRoot", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy", "SSL_CERT_FILE", "SSL_CERT_DIR",
    ];

    private readonly ResticRepoInfo _repo;
    private readonly string _exe;
    private readonly string _cacheDir;
    private bool _ready;

    public ResticBackupStorage(ResticRepoInfo repo, string resticPath, string cacheDir)
    {
        if (ResticSettings.ValidateRepository(repo.Repository) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        // The stored keys were checked when the target was saved; check again so a changed database row cannot smuggle in RESTIC_PASSWORD_COMMAND.
        if (repo.Env.Keys.FirstOrDefault(k => !ResticSettings.IsAllowedEnvName(k)) is { } bad)
        {
            throw new InvalidOperationException($"{bad} is not an access-key variable restic may be given.");
        }

        _repo = repo with { Repository = repo.Repository.Trim() };
        _exe = string.IsNullOrWhiteSpace(resticPath) ? "restic" : resticPath;
        _cacheDir = cacheDir;
    }

    public async Task<Stream> OpenWriteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        RequireFileName(fileName);
        await EnsureRepositoryAsync(cancellationToken);

        var p = Start(["backup", "--stdin", "--stdin-filename", fileName, "--tag", ResticSettings.FileTagPrefix + fileName,
            "--host", ResticSettings.HostName, "--quiet"], redirectStdin: true);
        return new WriteStream(p, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        RequireFileName(fileName);

        // Look first, so a missing file is a FileNotFoundException like on the other targets, not an error in the middle of a restore.
        if ((await FindSnapshotsAsync(fileName, cancellationToken)).Count == 0)
        {
            throw new FileNotFoundException("The backup file no longer exists in the restic repository.", fileName);
        }

        var p = Start(["dump", "--tag", ResticSettings.FileTagPrefix + fileName, "--host", ResticSettings.HostName, "latest", "/" + fileName],
            redirectStdin: false);
        return new ReadStream(p);
    }

    public async Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        RequireFileName(fileName);
        var ids = (await FindSnapshotsAsync(fileName, cancellationToken)).Select(s => s.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // Forget only; the space is freed by the maintenance (prune), which is slow on a remote repository and would run after every retention step.
        var (exit, _, err) = await RunAsync(["forget", "--quiet", .. ids], cancellationToken);
        if (exit != 0)
        {
            throw new IOException($"restic could not forget the snapshot: {Describe(exit, err)}");
        }
    }

    public async Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var (exit, stdout, err) = await RunAsync(["snapshots", "--json", "--host", ResticSettings.HostName], cancellationToken);
        if (exit == 10)
        {
            return Array.Empty<BackupFileInfo>(); // a repository that does not exist yet holds nothing
        }
        if (exit != 0)
        {
            throw new IOException($"restic could not list the snapshots: {Describe(exit, err)}");
        }

        return ParseSnapshots(stdout)
            .Select(s => new BackupFileInfo(s.FileName, s.Bytes, s.TimeUtc))
            .ToList();
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        // Initializes an empty repository (a new bucket or folder) so the first backup does not have to.
        await EnsureRepositoryAsync(cancellationToken);
    }

    /// <summary>
    /// The maintenance of the repository: <c>prune</c> frees the space of snapshots that were forgotten (every deleted or expired backup),
    /// <c>check</c> verifies the repository's structure. A scheduled action runs this.
    /// </summary>
    public async Task<(bool Ok, string Summary)> MaintainAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureRepositoryAsync(cancellationToken);

            var prune = await RunAsync(["prune", "--quiet"], cancellationToken);
            if (prune.Exit != 0)
            {
                return (false, $"prune failed: {Describe(prune.Exit, prune.Err)}");
            }

            var check = await RunAsync(["check", "--quiet"], cancellationToken);
            return check.Exit == 0
                ? (true, "Pruned the repository and checked it: no errors.")
                : (false, $"Pruned, but check reported a problem: {Describe(check.Exit, check.Err)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    // ---- snapshots ----

    private async Task<IReadOnlyList<ResticSnapshot>> FindSnapshotsAsync(string fileName, CancellationToken ct)
    {
        var (exit, stdout, err) = await RunAsync(["snapshots", "--json", "--host", ResticSettings.HostName,
            "--tag", ResticSettings.FileTagPrefix + fileName], ct);
        if (exit == 10)
        {
            return Array.Empty<ResticSnapshot>();
        }
        if (exit != 0)
        {
            throw new IOException($"restic could not look up the backup: {Describe(exit, err)}");
        }

        return ParseSnapshots(stdout);
    }

    /// <summary>Reads <c>restic snapshots --json</c>: the snapshots that carry an <c>mdfile:</c> tag, oldest first.</summary>
    public static IReadOnlyList<ResticSnapshot> ParseSnapshots(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ResticSnapshot>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ResticSnapshot>();
            }

            var list = new List<ResticSnapshot>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var fileName = s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t is not null && t.StartsWith(ResticSettings.FileTagPrefix, StringComparison.Ordinal))
                    : null;
                if (fileName is null || !s.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } snapshotId)
                {
                    continue; // not one of ours
                }

                DateTime? time = s.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                                 && DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : null;
                var bytes = s.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object
                            && summary.TryGetProperty("total_bytes_processed", out var b) && b.TryGetInt64(out var n)
                    ? n
                    : 0;
                list.Add(new ResticSnapshot(snapshotId, fileName[ResticSettings.FileTagPrefix.Length..], time, bytes));
            }

            return list.OrderBy(s => s.TimeUtc ?? DateTime.MinValue).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<ResticSnapshot>();
        }
    }

    // ---- the child process ----

    private async Task EnsureRepositoryAsync(CancellationToken ct)
    {
        if (_ready)
        {
            return;
        }

        var (exit, _, err) = await RunAsync(["cat", "config"], ct);
        if (exit == 10)
        {
            var init = await RunAsync(["init", "--quiet"], ct);
            if (init.Exit != 0)
            {
                throw new IOException($"restic could not create the repository: {Describe(init.Exit, init.Err)}");
            }
        }
        else if (exit != 0)
        {
            throw new IOException(Describe(exit, err));
        }

        _ready = true;
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using var p = Start(args, redirectStdin: false);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            throw;
        }

        return (p.ExitCode, await stdout, await stderr);
    }

    private Process Start(IReadOnlyList<string> args, bool redirectStdin)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        foreach (var name in PassThrough)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                psi.Environment[name] = value;
            }
        }

        psi.Environment["RESTIC_REPOSITORY"] = _repo.Repository;
        psi.Environment["RESTIC_PASSWORD"] = _repo.Password;
        psi.Environment["RESTIC_CACHE_DIR"] = _cacheDir;
        foreach (var (name, value) in _repo.Env)
        {
            psi.Environment[name] = value;
        }

        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("restic did not start.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("restic is not installed where MatDock runs (the MatDock image includes it; otherwise set MatDock:ResticPath).");
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }

    private static void RequireFileName(string fileName)
    {
        if (!ResticSettings.IsValidFileName(fileName))
        {
            throw new ArgumentException($"Invalid backup file name: '{fileName}'.", nameof(fileName));
        }
    }

    /// <summary>What a restic exit code and its messages say, in a line (restic's own words for anything unknown).</summary>
    public static string Describe(int exit, string stderr) => exit switch
    {
        10 => "The restic repository does not exist.",
        11 => "The repository is locked by another restic process; try again later.",
        12 => "Wrong repository password.",
        _ => FirstLines(stderr) is { Length: > 0 } text ? text : $"restic exited with code {exit}.",
    };

    private static string FirstLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3);
        var joined = string.Join(" ", lines);
        return joined.Length > 300 ? joined[..299] + "…" : joined;
    }

    // ---- streams ----

    /// <summary>The write side: bytes go to restic's stdin; closing the stream ends the snapshot and reports restic's result.</summary>
    private sealed class WriteStream : Stream
    {
        private readonly Process _p;
        private readonly Stream _stdin;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly CancellationToken _ct;
        private bool _faulted;
        private bool _closed;

        public WriteStream(Process p, CancellationToken ct)
        {
            _p = p;
            _ct = ct;
            _stdin = p.StandardInput.BaseStream;
            // Drain both, or restic blocks on a full pipe.
            _stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _stdin.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            try { _stdin.Write(buffer, offset, count); }
            catch { _faulted = true; throw; }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await _stdin.WriteAsync(buffer, cancellationToken); }
            catch { _faulted = true; throw; }
        }

        public override async ValueTask DisposeAsync()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_faulted)
            {
                Kill(_p); // a broken write: no snapshot of half a tar
                _p.Dispose();
                return;
            }

            try
            {
                await _stdin.FlushAsync(_ct);
                _stdin.Close(); // end of input: restic finishes the snapshot
                await _p.WaitForExitAsync(_ct);
            }
            catch (OperationCanceledException)
            {
                Kill(_p);
                throw;
            }

            var err = await _stderr;
            await _stdout;
            var exit = _p.ExitCode;
            _p.Dispose();
            if (exit != 0)
            {
                throw new IOException($"restic could not write the backup: {Describe(exit, err)}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Disposed without DisposeAsync (a sync using): the caller cannot learn the result, so abort rather than save a snapshot nobody checked.
            if (disposing && !_closed)
            {
                _closed = true;
                Kill(_p);
                _p.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The read side: restic's stdout. The end of the stream is only the end when restic exited cleanly; otherwise the read fails, so a cut-off restore is not mistaken for a whole one.</summary>
    private sealed class ReadStream : Stream
    {
        private readonly Process _p;
        private readonly Stream _stdout;
        private readonly Task<string> _stderr;
        private bool _eof;
        private bool _closed;

        public ReadStream(Process p)
        {
            _p = p;
            _stdout = p.StandardOutput.BaseStream;
            _stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _stdout.Read(buffer, offset, count);
            if (n == 0 && !_eof)
            {
                _eof = true;
                FinishAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _stdout.ReadAsync(buffer, cancellationToken);
            if (n == 0 && !_eof)
            {
                _eof = true;
                await FinishAsync(cancellationToken);
            }

            return n;
        }

        private async Task FinishAsync(CancellationToken ct)
        {
            await _p.WaitForExitAsync(ct);
            var err = await _stderr;
            if (_p.ExitCode != 0)
            {
                throw new IOException($"restic could not read the backup: {Describe(_p.ExitCode, err)}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                Kill(_p); // stopped reading early (or done): nothing more to wait for
                _p.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
