using System.Text;
using LibGit2Sharp;
using MatDock.Core.Ssh;
using Renci.SshNet;

namespace MatDock.Core.Git;

/// <summary>Git URLs MatDock clones: https (token) and ssh (key), in both ssh forms.</summary>
public static class GitUrls
{
    /// <summary><c>http(s)://…</c>, <c>ssh://[user@]host[:port]/path</c> or <c>user@host:path</c> (the scp form).</summary>
    public static bool IsSupported(string? url)
    {
        var u = (url ?? string.Empty).Trim();
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || IsSsh(u);
    }

    public static bool IsSsh(string? url)
    {
        var u = (url ?? string.Empty).Trim();
        if (u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 && parsed.AbsolutePath.Length > 1;
        }

        // scp form: user@host:path (no scheme, a colon before the first slash, no spaces).
        var at = u.IndexOf('@');
        var colon = u.IndexOf(':');
        return at > 0 && colon > at + 1 && colon < u.Length - 1 && !u.Contains("://", StringComparison.Ordinal) && !u.Contains(' ')
               && (u.IndexOf('/') < 0 || u.IndexOf('/') > colon);
    }

    /// <summary>The scp form as an <c>ssh://</c> URL (the path keeps its form: <c>git@host:org/repo</c> → <c>ssh://git@host/org/repo</c>).</summary>
    public static string ToSshUri(string url)
    {
        var u = url.Trim();
        if (u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return u;
        }

        var colon = u.IndexOf(':');
        return "ssh://" + u[..colon] + "/" + u[(colon + 1)..].TrimStart('/');
    }

    public const string Requirement = "Git URL must start with http://, https:// or ssh://, or be like git@host:org/repo.git.";
}

/// <summary>What the SSH transport needs for one clone: the key, and the host key the server must show (null = learn it).</summary>
public sealed class SshGitContext
{
    [ThreadStatic] private static SshGitContext? _current;

    public SshGitContext(string? privateKeyPem, string? passphrase, string? hostKeyFingerprint)
    {
        PrivateKeyPem = privateKeyPem;
        Passphrase = passphrase;
        HostKeyFingerprint = hostKeyFingerprint;
    }

    public string? PrivateKeyPem { get; }
    public string? Passphrase { get; }
    public string? HostKeyFingerprint { get; }

    /// <summary>The host key the server showed (set by the transport).</summary>
    public string? SeenHostKey { get; internal set; }

    public bool HostKeyRejected { get; internal set; }

    /// <summary>The context of the clone running on this thread (LibGit2Sharp calls the transport on the thread of <c>Clone</c>).</summary>
    internal static SshGitContext? Current => _current;

    /// <summary>Sets <paramref name="context"/> for the clone that runs on this thread until the scope ends.</summary>
    public static IDisposable Use(SshGitContext context)
    {
        SshGitTransport.EnsureRegistered();
        _current = context;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _current = null;
    }
}

/// <summary>
/// Git over SSH for LibGit2Sharp, which can speak ssh but takes no key: the <c>ssh://</c> transport is replaced by one that runs
/// <c>git-upload-pack</c> on the server through SSH.NET (the library MatDock uses for its hosts) with the key of the Git credentials,
/// and checks the host key like the environments do (the first one is saved, another one is refused). Fetch only (clone, ls-remote).
/// </summary>
public sealed class SshGitTransport : SmartSubtransport
{
    private static readonly object Gate = new();
    private static bool _registered;
    private UploadPackStream? _stream;

    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (!_registered)
            {
                GlobalSettings.RegisterSmartSubtransport<SshGitTransport>("ssh");
                _registered = true;
            }
        }
    }

    protected override SmartSubtransportStream Action(string url, GitSmartSubtransportAction action)
    {
        switch (action)
        {
            case GitSmartSubtransportAction.UploadPackList:
                _stream?.Dispose();
                _stream = new UploadPackStream(this, url, SshGitContext.Current ?? throw new InvalidOperationException("SSH Git needs the key of the Git credentials."));
                return _stream;
            case GitSmartSubtransportAction.UploadPack:
                // Not RPC: the pack comes over the same connection as the list of references.
                return _stream ?? throw new InvalidOperationException("The SSH connection to the Git server is gone.");
            default:
                throw new NotSupportedException("MatDock only reads from Git repositories (no push).");
        }
    }

    protected override void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>The <c>git-upload-pack</c> command for the path of <paramref name="uri"/> (the path quoted for the server's shell).</summary>
    public static string UploadPackCommand(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        // ssh://host/~/repo means the home of the user, like git does.
        if (path.StartsWith("/~", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        return "git-upload-pack '" + path.Replace("'", "'\\''") + "'";
    }

    private sealed class UploadPackStream : SmartSubtransportStream
    {
        private readonly SshClient _client;
        private readonly SshCommand _command;
        private readonly Stream _output;
        private readonly Stream _input;
        private readonly IAsyncResult _running;

        public UploadPackStream(SmartSubtransport transport, string url, SshGitContext context)
            : base(transport)
        {
            var uri = new Uri(url);
            if (string.IsNullOrWhiteSpace(context.PrivateKeyPem))
            {
                throw new InvalidOperationException("SSH Git URLs need Git credentials with an SSH key.");
            }

            var user = string.IsNullOrEmpty(uri.UserInfo) ? "git" : Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
            using var key = new MemoryStream(Encoding.UTF8.GetBytes(context.PrivateKeyPem));
            var keyFile = string.IsNullOrEmpty(context.Passphrase) ? new PrivateKeyFile(key) : new PrivateKeyFile(key, context.Passphrase);
            var info = new ConnectionInfo(uri.Host, uri.Port > 0 ? uri.Port : 22, user, new PrivateKeyAuthenticationMethod(user, keyFile))
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            _client = new SshClient(info);
            _client.HostKeyReceived += (_, e) =>
            {
                var seen = HostKeyCheck.Format(e.FingerPrintSHA256);
                context.SeenHostKey = seen;
                e.CanTrust = HostKeyCheck.Trusts(context.HostKeyFingerprint, seen);
                context.HostKeyRejected = !e.CanTrust;
            };
            _client.Connect();

            _command = _client.CreateCommand(UploadPackCommand(uri));
            _command.CommandTimeout = TimeSpan.FromMinutes(30);
            _running = _command.BeginExecute();
            _output = _command.OutputStream;
            _input = _command.CreateInputStream();
        }

        public override int Read(Stream dataStream, long length, out long bytesRead)
        {
            var buffer = new byte[Math.Min(length, 64 * 1024)];
            var read = _output.Read(buffer, 0, buffer.Length);
            dataStream.Write(buffer, 0, read);
            bytesRead = read;
            return 0;
        }

        public override int Write(Stream dataStream, long length)
        {
            var buffer = new byte[64 * 1024];
            long left = length;
            int read;
            while (left > 0 && (read = dataStream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                _input.Write(buffer, 0, read);
                left -= read;
            }

            _input.Flush();
            return 0;
        }

        protected override void Free()
        {
            Dispose();
            base.Free();
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _input.Dispose(); } catch { /* closed */ }
            try { _command.EndExecute(_running); } catch { /* ended */ }
            _command.Dispose();
            try { _client.Disconnect(); } catch { /* gone */ }
            _client.Dispose();
        }
    }
}
