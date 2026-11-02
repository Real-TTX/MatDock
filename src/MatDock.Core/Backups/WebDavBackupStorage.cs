using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace MatDock.Core.Backups;

/// <summary>Decrypted connection details for a WebDAV target: the folder's address and the sign-in.</summary>
public sealed record WebDavConnectionInfo(string Url, string? Username, string? Password);

/// <summary>
/// Stores backups in a folder of a WebDAV server (Nextcloud, ownCloud, a NAS, Apache or nginx with WebDAV): <c>PUT</c> streams an archive while it is made
/// (chunked, nothing is spooled to disk), <c>GET</c> reads it back, <c>PROPFIND</c> lists the folder, <c>MKCOL</c> creates it (and its parents) when it is
/// missing. Basic authentication; the password is stored encrypted with the target.
/// </summary>
public sealed class WebDavBackupStorage : IBackupStorage
{
    private static readonly XNamespace Dav = "DAV:";

    // One pool of connections for every WebDAV target (a storage is made per backup and not disposed).
    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    private readonly WebDavConnectionInfo _info;
    private readonly HttpClient _http;
    private readonly Uri _folder;

    public WebDavBackupStorage(WebDavConnectionInfo info, HttpMessageHandler? handler = null)
    {
        _info = info;
        _folder = Folder(info.Url);
        _http = new HttpClient(handler ?? SharedHandler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // an archive of many GB takes long; every call has its own token
        if (!string.IsNullOrEmpty(info.Username))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{info.Username}:{info.Password}")));
        }
    }

    /// <summary>The folder's address with a trailing slash (so file names resolve inside it).</summary>
    public static Uri Folder(string url)
    {
        var text = url.Trim();
        return new Uri(text.EndsWith('/') ? text : text + "/", UriKind.Absolute);
    }

    /// <summary>Why an address cannot be a WebDAV folder, or null.</summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return "Please enter the address of the WebDAV folder (https://…), e.g. https://cloud.example.com/remote.php/dav/files/me/Backups.";
        }

        return string.IsNullOrEmpty(uri.UserInfo) ? null : "Put the user name and password into their fields, not into the address.";
    }

    public async Task<Stream> OpenWriteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        await EnsureFolderAsync(cancellationToken);
        var pipe = new Pipe();
        var content = new StreamContent(pipe.Reader.AsStream());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Put, FileUri(fileName)) { Content = content };
        request.Headers.TransferEncodingChunked = true;
        var upload = SendAsync(request, cancellationToken);
        return new UploadStream(pipe.Writer, upload, fileName);
    }

    public async Task<Stream> OpenReadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync(FileUri(fileName), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new FileNotFoundException("The backup file no longer exists on the WebDAV server.", fileName);
        }

        await EnsureSuccessAsync(response, "read", fileName);
        return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    public async Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, FileUri(fileName)), cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, "delete", fileName);
        }
    }

    public async Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await PropfindAsync(_folder, depth: "1", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<BackupFileInfo>();
        }

        await EnsureSuccessAsync(response, "list", null);
        return ParseListing(await response.Content.ReadAsStringAsync(cancellationToken), _folder);
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFolderAsync(cancellationToken);
        var probe = $".write-test-{Guid.NewGuid():N}";
        using (var put = await _http.PutAsync(FileUri(probe), new StringContent("ok"), cancellationToken))
        {
            await EnsureSuccessAsync(put, "write", probe);
        }

        await DeleteAsync(probe, cancellationToken);
    }

    /// <summary>The files of a <c>PROPFIND</c> answer (not the folder itself, not sub-folders, not MatDock's write tests).</summary>
    public static IReadOnlyList<BackupFileInfo> ParseListing(string xml, Uri folder)
    {
        var files = new List<BackupFileInfo>();
        foreach (var response in XDocument.Parse(xml).Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            var prop = response.Descendants(Dav + "prop").FirstOrDefault();
            if (href is null || prop is null || prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null)
            {
                continue;
            }

            var path = Uri.UnescapeDataString(new Uri(folder, href).AbsolutePath);
            var name = path[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
            if (name.Length == 0 || name.StartsWith(".write-test-", StringComparison.Ordinal))
            {
                continue;
            }

            var size = long.TryParse(prop.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            DateTime? modified = DateTime.TryParse(prop.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m) ? m : null;
            files.Add(new BackupFileInfo(name, size, modified));
        }

        return files;
    }

    /// <summary>The address of a file in the folder; only a bare file name is allowed (no <c>/</c>, no <c>..</c>).</summary>
    private Uri FileUri(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            throw new ArgumentException($"Invalid backup file name: '{fileName}'.", nameof(fileName));
        }

        return new Uri(_folder, Uri.EscapeDataString(name));
    }

    /// <summary>Creates the folder (and the ones above it, up to the first that exists) when it is missing.</summary>
    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        using (var probe = await PropfindAsync(_folder, depth: "0", ct))
        {
            if (probe.IsSuccessStatusCode || (int)probe.StatusCode == 207)
            {
                return;
            }

            if (probe.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(probe, "open the folder", null);
            }
        }

        // Missing: create it from the first missing parent down.
        var segments = _folder.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var built = new UriBuilder(_folder) { Path = "/" };
        foreach (var segment in segments)
        {
            built.Path = built.Path.TrimEnd('/') + "/" + segment + "/";
            using var mkcol = await _http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), built.Uri), ct);
            // 405: it is there already; 201: made now. Anything else on the last one is a real error.
            if (!mkcol.IsSuccessStatusCode && mkcol.StatusCode != HttpStatusCode.MethodNotAllowed && built.Uri == _folder)
            {
                await EnsureSuccessAsync(mkcol, "create the folder", null);
            }
        }
    }

    private Task<HttpResponseMessage> PropfindAsync(Uri uri, string depth, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>",
                Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", depth);
        return _http.SendAsync(request, ct);
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await _http.SendAsync(request, ct))
        {
            await EnsureSuccessAsync(response, "write", Path.GetFileName(request.RequestUri!.AbsolutePath));
        }
    }

    private Task EnsureSuccessAsync(HttpResponseMessage response, string what, string? fileName)
    {
        if (response.IsSuccessStatusCode || (int)response.StatusCode == 207)
        {
            return Task.CompletedTask;
        }

        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "the user name or password is wrong",
            HttpStatusCode.Forbidden => "the user may not do that here",
            HttpStatusCode.NotFound => "the folder or file is not there",
            HttpStatusCode.InsufficientStorage => "the server is full",
            HttpStatusCode.RequestEntityTooLarge => "the server refuses a file this large",
            _ => $"{(int)response.StatusCode} {response.ReasonPhrase}",
        };
        throw new IOException($"WebDAV: could not {what}{(fileName is null ? string.Empty : $" '{fileName}'")} at {_info.Url}: {reason}.");
    }

    /// <summary>What the backup writes into: the pipe of the running upload. Disposing it ends the upload and reports a failure of the server.</summary>
    private sealed class UploadStream : Stream
    {
        private readonly PipeWriter _writer;
        private readonly Stream _inner;
        private readonly Task _upload;
        private readonly string _fileName;
        private bool _done;

        public UploadStream(PipeWriter writer, Task upload, string fileName)
        {
            _writer = writer;
            _inner = writer.AsStream();
            _upload = upload;
            _fileName = fileName;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_upload.IsCompleted)
            {
                await _upload; // the server stopped taking it: say why
                throw new IOException($"WebDAV: the upload of '{_fileName}' ended early.");
            }

            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_done)
            {
                _done = true;
                await _writer.CompleteAsync();
                await _upload;
            }

            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                _done = true;
                _writer.Complete();
                _upload.GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The body of a download; disposing it ends the response.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
