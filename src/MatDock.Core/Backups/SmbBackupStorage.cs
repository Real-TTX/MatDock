using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;

namespace MatDock.Core.Backups;

/// <summary>Decrypted connection details for an SMB target.</summary>
public sealed record SmbConnectionInfo(string Host, string Share, string? Directory, string? Username, string? Password, string? Domain);

/// <summary>
/// Stores backups on an SMB/CIFS network share (NAS) using the pure-C# SMBLibrary client — no OS mount
/// needed in the container. NOTE: exercised against a real NAS is still to be verified.
/// </summary>
public sealed class SmbBackupStorage : IBackupStorage
{
    private readonly SmbConnectionInfo _info;

    public SmbBackupStorage(SmbConnectionInfo info)
    {
        _info = info;
    }

    public Task<Stream> OpenWriteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var session = SmbSession.Open(_info);
        try
        {
            session.EnsureDirectory(_info.Directory);
            var path = BuildPath(_info.Directory, fileName);
            var status = session.FileStore.CreateFile(out var handle, out _, path,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
                ShareAccess.Read, CreateDisposition.FILE_OVERWRITE_IF,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"SMB: Datei '{path}' konnte nicht angelegt werden ({status}).");
            }

            return Task.FromResult<Stream>(new SmbWriteStream(session, handle, ChunkSize(session.Client.MaxWriteSize)));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var session = SmbSession.Open(_info);
        try
        {
            var path = BuildPath(_info.Directory, fileName);
            var status = session.FileStore.CreateFile(out var handle, out _, path,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
                ShareAccess.Read, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"SMB: Datei '{path}' konnte nicht geöffnet werden ({status}).");
            }

            return Task.FromResult<Stream>(new SmbReadStream(session, handle, ChunkSize(session.Client.MaxReadSize)));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        using var session = SmbSession.Open(_info);
        var path = BuildPath(_info.Directory, fileName);
        var status = session.FileStore.CreateFile(out var handle, out _, path,
            AccessMask.DELETE, SMBLibrary.FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DELETE_ON_CLOSE, null);
        if (status == NTStatus.STATUS_SUCCESS)
        {
            session.FileStore.CloseFile(handle); // FILE_DELETE_ON_CLOSE removes it
        }

        return Task.CompletedTask;
    }

    public Task TestAsync(CancellationToken cancellationToken = default)
    {
        using var session = SmbSession.Open(_info);
        session.EnsureDirectory(_info.Directory);
        return Task.CompletedTask;
    }

    private static int ChunkSize(uint negotiated)
        // Never exceed the negotiated maximum; only cap the upper bound for memory.
        => negotiated == 0 ? 65536 : (int)Math.Min(negotiated, 1_048_576u);

    private static string BuildPath(string? directory, string fileName)
    {
        var name = Path.GetFileName(fileName);
        var dir = (directory ?? string.Empty).Replace('/', '\\').Trim('\\');
        return dir.Length == 0 ? name : $"{dir}\\{name}";
    }
}

/// <summary>An open SMB connection (client + tree). Dispose closes tree, logoff and disconnect.</summary>
internal sealed class SmbSession : IDisposable
{
    public required SMB2Client Client { get; init; }
    public required ISMBFileStore FileStore { get; init; }

    public static SmbSession Open(SmbConnectionInfo info)
    {
        var client = new SMB2Client();
        var address = ResolveHost(info.Host);
        if (!client.Connect(address, SMBTransportType.DirectTCPTransport))
        {
            throw new IOException($"SMB: Keine Verbindung zu {info.Host} (Port 445).");
        }

        var login = client.Login(info.Domain ?? string.Empty, info.Username ?? string.Empty, info.Password ?? string.Empty);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new IOException($"SMB-Anmeldung fehlgeschlagen ({login}).");
        }

        var fileStore = client.TreeConnect(info.Share, out var status);
        if (status != NTStatus.STATUS_SUCCESS || fileStore is null)
        {
            client.Logoff();
            client.Disconnect();
            throw new IOException($"SMB-Freigabe '{info.Share}' nicht erreichbar ({status}).");
        }

        return new SmbSession { Client = client, FileStore = fileStore };
    }

    /// <summary>Creates the directory tree (best effort) so the file can be written.</summary>
    public void EnsureDirectory(string? directory)
    {
        var dir = (directory ?? string.Empty).Replace('/', '\\').Trim('\\');
        if (dir.Length == 0)
        {
            return;
        }

        var cumulative = string.Empty;
        foreach (var segment in dir.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cumulative = cumulative.Length == 0 ? segment : $"{cumulative}\\{segment}";
            var status = FileStore.CreateFile(out var handle, out _, cumulative,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN_IF,
                CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                FileStore.CloseFile(handle);
            }
        }
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip;
        }

        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new IOException($"SMB: Host '{host}' konnte nicht aufgelöst werden.");
    }

    public void Dispose()
    {
        try { FileStore.Disconnect(); } catch { /* best effort */ }
        try { Client.Logoff(); } catch { /* best effort */ }
        try { Client.Disconnect(); } catch { /* best effort */ }
    }
}

/// <summary>Write-only stream over an SMB file handle.</summary>
internal sealed class SmbWriteStream : Stream
{
    private readonly SmbSession _session;
    private readonly object _handle;
    private readonly int _chunk;
    private long _offset;

    public SmbWriteStream(SmbSession session, object handle, int chunk)
    {
        _session = session;
        _handle = handle;
        _chunk = chunk;
    }

    public override bool CanWrite => true;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _offset; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var done = 0;
        while (done < count)
        {
            var size = Math.Min(count - done, _chunk);
            var slice = new byte[size];
            Array.Copy(buffer, offset + done, slice, 0, size);
            var status = _session.FileStore.WriteFile(out var written, _handle, _offset, slice);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"SMB WriteFile fehlgeschlagen ({status}).");
            }
            if (written <= 0)
            {
                throw new IOException("SMB WriteFile schrieb 0 Bytes.");
            }

            _offset += written;
            done += written;
        }
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _session.FileStore.CloseFile(_handle); } catch { /* best effort */ }
            _session.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Read-only stream over an SMB file handle.</summary>
internal sealed class SmbReadStream : Stream
{
    private readonly SmbSession _session;
    private readonly object _handle;
    private readonly int _chunk;
    private long _offset;
    private byte[] _leftover = Array.Empty<byte>();
    private int _leftoverPos;

    public SmbReadStream(SmbSession session, object handle, int chunk)
    {
        _session = session;
        _handle = handle;
        _chunk = chunk;
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _offset; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_leftoverPos < _leftover.Length)
        {
            var n = Math.Min(count, _leftover.Length - _leftoverPos);
            Array.Copy(_leftover, _leftoverPos, buffer, offset, n);
            _leftoverPos += n;
            return n;
        }

        var status = _session.FileStore.ReadFile(out var data, _handle, _offset, _chunk);
        if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
        {
            return 0;
        }
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"SMB ReadFile fehlgeschlagen ({status}).");
        }

        _offset += data.Length;
        var copied = Math.Min(count, data.Length);
        Array.Copy(data, 0, buffer, offset, copied);
        if (copied < data.Length)
        {
            _leftover = data;
            _leftoverPos = copied;
        }

        return copied;
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _session.FileStore.CloseFile(_handle); } catch { /* best effort */ }
            _session.Dispose();
        }

        base.Dispose(disposing);
    }
}
