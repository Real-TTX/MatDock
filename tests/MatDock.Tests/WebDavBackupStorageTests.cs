using System.Net;
using System.Text;
using MatDock.Core.Backups;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests;

/// <summary>Backups in a folder of a WebDAV server: against a small WebDAV server in memory (PUT, GET, DELETE, PROPFIND, MKCOL, Basic sign-in).</summary>
public class WebDavBackupStorageTests
{
    /// <summary>What a WebDAV server keeps: folders and files by path; a request without the right sign-in gets 401.</summary>
    private sealed class FakeDav : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal) { "/" };
        public List<string> Log { get; } = [];
        public string Auth { get; set; } = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:secret"));
        public bool ChunkedSeen { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Log.Add($"{request.Method} {path}");
            if (request.Headers.Authorization?.ToString() != Auth)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var folder = path.EndsWith('/') ? path : path + "/";
            var parent = path.TrimEnd('/')[..(path.TrimEnd('/').LastIndexOf('/') + 1)];
            switch (request.Method.Method)
            {
                case "PUT":
                    if (!Folders.Contains(parent))
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }

                    ChunkedSeen |= request.Headers.TransferEncodingChunked == true;
                    Files[path] = await request.Content!.ReadAsByteArrayAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "GET":
                    return Files.TryGetValue(path, out var bytes)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                case "DELETE":
                    return new HttpResponseMessage(Files.Remove(path) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
                case "MKCOL":
                    if (Folders.Contains(folder))
                    {
                        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                    }

                    if (!Folders.Contains(parent))
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }

                    Folders.Add(folder);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "PROPFIND":
                    if (!Folders.Contains(folder))
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    var xml = new StringBuilder("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">");
                    xml.Append($"<d:response><d:href>{folder}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>");
                    if (request.Headers.GetValues("Depth").Single() == "1")
                    {
                        foreach (var (file, content) in Files.Where(f => f.Key.StartsWith(folder, StringComparison.Ordinal) && !f.Key[folder.Length..].Contains('/')))
                        {
                            xml.Append($"<d:response><d:href>{Uri.EscapeDataString(file).Replace("%2F", "/")}</d:href><d:propstat><d:prop><d:resourcetype/>"
                                       + $"<d:getcontentlength>{content.Length}</d:getcontentlength><d:getlastmodified>Sun, 01 Nov 2026 10:00:00 GMT</d:getlastmodified>"
                                       + "</d:prop></d:propstat></d:response>");
                        }
                    }

                    xml.Append("</d:multistatus>");
                    return new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
        }
    }

    private static WebDavBackupStorage Storage(FakeDav dav, string user = "me", string password = "secret")
        => new(new WebDavConnectionInfo("https://dav.example.com/remote.php/dav/files/me/Backups/MatDock", user, password), dav);

    [Fact]
    public async Task An_archive_goes_up_streamed_into_a_folder_that_is_made_and_comes_back_the_same()
    {
        var dav = new FakeDav();
        var storage = Storage(dav);
        var archive = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(archive);

        await using (var write = await storage.OpenWriteAsync("pgdata-20261101.tar.gz"))
        {
            for (var offset = 0; offset < archive.Length; offset += 65536)
            {
                await write.WriteAsync(archive.AsMemory(offset, Math.Min(65536, archive.Length - offset)));
            }
        }

        Assert.True(dav.ChunkedSeen); // streamed, not buffered first
        Assert.Contains("/remote.php/dav/files/me/Backups/MatDock/", dav.Folders);
        Assert.Contains(dav.Log, l => l == "MKCOL /remote.php/dav/files/me/Backups/");

        var listed = Assert.Single(await storage.ListAsync());
        Assert.Equal("pgdata-20261101.tar.gz", listed.FileName);
        Assert.Equal(archive.Length, listed.SizeBytes);
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), listed.ModifiedUtc);

        await using (var read = await storage.OpenReadAsync("pgdata-20261101.tar.gz"))
        {
            var back = new MemoryStream();
            await read.CopyToAsync(back);
            Assert.Equal(archive, back.ToArray());
        }

        await storage.DeleteAsync("pgdata-20261101.tar.gz");
        Assert.Empty(await storage.ListAsync());
        await storage.DeleteAsync("pgdata-20261101.tar.gz"); // gone already: no error
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync("pgdata-20261101.tar.gz"));
    }

    [Fact]
    public async Task The_test_writes_and_removes_a_probe_and_a_wrong_password_is_said_plainly()
    {
        var dav = new FakeDav();
        await Storage(dav).TestAsync();
        Assert.Empty(dav.Files);
        Assert.Contains(dav.Log, l => l.StartsWith("PUT /remote.php/dav/files/me/Backups/MatDock/.write-test-", StringComparison.Ordinal));

        var refused = await Assert.ThrowsAsync<IOException>(() => Storage(dav, password: "wrong").TestAsync());
        Assert.Contains("the user name or password is wrong", refused.Message);
    }

    [Fact]
    public async Task Only_a_bare_file_name_reaches_the_server()
    {
        var dav = new FakeDav();
        var storage = Storage(dav);
        await using (var write = await storage.OpenWriteAsync("../../escape.tar"))
        {
            await write.WriteAsync(new byte[] { 1 });
        }

        Assert.Contains("/remote.php/dav/files/me/Backups/MatDock/escape.tar", dav.Files.Keys);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(".."));
    }

    [Theory]
    [InlineData("https://cloud.example.com/remote.php/dav/files/me/Backups", true)]
    [InlineData("http://nas.local:5005/backups", true)]
    [InlineData("ftp://nas.local/backups", false)]
    [InlineData("https://me:pw@cloud.example.com/dav", false)]
    [InlineData("", false)]
    public void The_address_is_an_http_folder_without_credentials(string url, bool valid)
        => Assert.Equal(valid, WebDavBackupStorage.Problem(url) is null);

    [Fact]
    public void A_target_of_type_WebDav_is_validated_and_made_into_its_storage()
    {
        Assert.NotNull(BackupTargetService.ValidateInput(new BackupTargetInput { Name = "dav", Type = BackupTargetType.WebDav, WebDavUrl = "nas" }));
        Assert.Null(BackupTargetService.ValidateInput(new BackupTargetInput { Name = "dav", Type = BackupTargetType.WebDav, WebDavUrl = "https://nas.local/dav" }));

        var factory = new BackupStorageFactory(new MatDock.Core.Configuration.AppPaths(Path.GetTempPath()), new FakeSecretProtector(),
            Microsoft.Extensions.Options.Options.Create(new MatDock.Core.Configuration.MatDockOptions()));
        Assert.IsType<WebDavBackupStorage>(factory.Create(new BackupTarget { Type = BackupTargetType.WebDav, WebDavUrl = "https://nas.local/dav" }));
    }
}
