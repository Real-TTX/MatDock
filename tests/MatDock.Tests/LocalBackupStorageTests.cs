using System.Text;
using MatDock.Core.Backups;
using MatDock.Core.Configuration;
using Xunit;

namespace MatDock.Tests;

public class LocalBackupStorageTests
{
    [Fact]
    public async Task Write_read_delete_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "matdock-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalBackupStorage(new AppPaths(dir));
            var payload = Encoding.UTF8.GetBytes("hello backup");

            await using (var w = await storage.OpenWriteAsync("f.tar"))
            {
                await w.WriteAsync(payload);
            }

            using var ms = new MemoryStream();
            await using (var r = await storage.OpenReadAsync("f.tar"))
            {
                await r.CopyToAsync(ms);
            }
            Assert.Equal(payload, ms.ToArray());

            await storage.TestAsync();
            await storage.DeleteAsync("f.tar");

            await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync("f.tar"));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Path_traversal_in_file_name_is_neutralized()
    {
        var dir = Path.Combine(Path.GetTempPath(), "matdock-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalBackupStorage(new AppPaths(dir));
            await using (var w = await storage.OpenWriteAsync("../escape.tar"))
            {
                await w.WriteAsync(new byte[] { 1 });
            }

            // The file must land inside the backups dir (as "escape.tar"), not the parent.
            Assert.True(File.Exists(Path.Combine(dir, "backups", "escape.tar")));
            Assert.False(File.Exists(Path.Combine(dir, "escape.tar")));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }
}
