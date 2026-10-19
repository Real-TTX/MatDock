using System.Formats.Tar;
using System.Text;
using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class ContainerCopyFilesTests
{
    private static MemoryStream Tar(params (string Name, string? Text)[] entries)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = new PaxTarEntry(text is null ? TarEntryType.Directory : TarEntryType.RegularFile, name);
                if (text is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                }

                writer.WriteEntry(entry);
            }
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_folder_lists_its_direct_children()
    {
        var (entries, truncated) = ContainerCopyFiles.ListFromTar(Tar(("./", null), ("./app", "binary"), ("./etc/", null), ("./etc/passwd", "root:x"), ("./etc/ssl/certs/ca.pem", "x")));
        Assert.False(truncated);
        Assert.Equal(["etc", "app"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.Equal(6, entries[1].Size);
    }

    [Fact]
    public async Task A_file_comes_out_of_its_tar_capped_for_the_editor()
    {
        var output = new MemoryStream();
        Assert.True(await ContainerCopyFiles.ExtractFileAsync(Tar(("hostname", "web-1\n")), output, 3, default));
        Assert.Equal("web", Encoding.UTF8.GetString(output.ToArray()));
        Assert.False(await ContainerCopyFiles.ExtractFileAsync(new MemoryStream(), new MemoryStream(), null, default));
    }

    [Fact]
    public async Task A_file_or_folder_goes_in_as_a_tar()
    {
        var ms = new MemoryStream();
        await ContainerCopyFiles.WriteTarAsync(ms, "app.conf", "listen 80;", default);
        ms.Position = 0;
        var entry = new TarReader(ms).GetNextEntry()!;
        Assert.Equal("app.conf", entry.Name);
        Assert.Equal("listen 80;", new StreamReader(entry.DataStream!).ReadToEnd());

        var folder = new MemoryStream();
        await ContainerCopyFiles.WriteTarAsync(folder, "cache", (Stream?)null, default);
        folder.Position = 0;
        Assert.Equal(TarEntryType.Directory, new TarReader(folder).GetNextEntry()!.EntryType);
    }

    [Fact]
    public void The_commands_quote_the_container_and_path()
    {
        Assert.Equal(VolumeCommands.PathPrefix + "docker cp 'web:/etc/.' -", ContainerCopyFiles.CopyOut("docker", "web", "/etc/", folder: true));
        Assert.Equal(VolumeCommands.PathPrefix + @"docker cp 'web:/it'\''s' -", ContainerCopyFiles.CopyOut("docker", "web", "/it's", folder: false));
        Assert.Equal(VolumeCommands.PathPrefix + "docker cp - 'web:/app/conf'", ContainerCopyFiles.CopyIn("docker", "web", "/app/conf"));
        Assert.True(ContainerCopyFiles.IsNoShell(127, ""));
        Assert.True(ContainerCopyFiles.IsNoShell(1, "OCI runtime exec failed: exec failed: unable to start container process: exec: \"sh\": executable file not found in $PATH"));
        Assert.False(ContainerCopyFiles.IsNoShell(1, "Error response from daemon: container is not running"));
    }
}
