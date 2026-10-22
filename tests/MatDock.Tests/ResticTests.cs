using System.Security.Cryptography;
using MatDock.Core.Backups;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

public class ResticTests
{
    // ---- the repository ----

    [Theory]
    [InlineData("s3:s3.amazonaws.com/my-bucket/matdock")]
    [InlineData("s3:http://minio.lan:9000/backups")]
    [InlineData("b2:bucket:path")]
    [InlineData("rest:https://backup.lan:8000/matdock")]
    [InlineData("rest:https://user:secret@backup.lan:8000/matdock")]
    [InlineData("azure:container:/path")]
    [InlineData("gs:bucket:/path")]
    [InlineData("/mnt/nas/restic-repo")]
    public void A_known_backend_or_an_absolute_path_is_a_repository(string repo)
        => Assert.Null(ResticSettings.ValidateRepository(repo));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("rclone:remote:path")]      // would start a program
    [InlineData("sftp:user@host:/path")]    // needs an SSH key inside MatDock
    [InlineData("-o sftp.command=evil")]    // a flag, not a repository
    [InlineData("/mnt/../etc")]
    [InlineData("s3:bucket\nnext")]
    [InlineData("ftp:host/path")]
    public void Anything_else_is_refused(string repo)
        => Assert.NotNull(ResticSettings.ValidateRepository(repo));

    [Fact]
    public void Credentials_in_a_url_are_not_shown()
    {
        Assert.Equal("rest:https://***@backup.lan:8000/r", ResticSettings.Redact("rest:https://user:secret@backup.lan:8000/r"));
        Assert.Equal("s3:s3.amazonaws.com/b", ResticSettings.Redact("s3:s3.amazonaws.com/b"));
        Assert.Equal(string.Empty, ResticSettings.Redact(null));
    }

    // ---- the access keys ----

    [Theory]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("B2_ACCOUNT_ID")]
    [InlineData("AZURE_ACCOUNT_KEY")]
    [InlineData("GOOGLE_PROJECT_ID")]
    [InlineData("OS_AUTH_URL")]
    [InlineData("RESTIC_REST_PASSWORD")]
    public void Access_keys_of_a_backend_are_allowed(string name)
        => Assert.True(ResticSettings.IsAllowedEnvName(name));

    [Theory]
    [InlineData("RESTIC_PASSWORD_COMMAND")] // runs a program to get the password
    [InlineData("RESTIC_PASSWORD")]
    [InlineData("RESTIC_REPOSITORY")]
    [InlineData("RESTIC_PASSWORD_FILE")]
    [InlineData("LD_PRELOAD")]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("aws_access_key_id")]
    [InlineData("AWS-KEY")]
    [InlineData("")]
    public void Anything_else_is_not_an_access_key(string name)
        => Assert.False(ResticSettings.IsAllowedEnvName(name));

    [Fact]
    public void ParseEnv_reads_lines_and_skips_blank_and_comment_lines()
    {
        var (env, error) = ResticSettings.ParseEnv("# s3\nAWS_ACCESS_KEY_ID = AKIA123\r\n\nAWS_SECRET_ACCESS_KEY=a=b=c\n");
        Assert.Null(error);
        Assert.Equal("AKIA123", env["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("a=b=c", env["AWS_SECRET_ACCESS_KEY"]); // only the first = separates
    }

    [Fact]
    public void ParseEnv_refuses_a_command_variable_and_never_echoes_a_secret()
    {
        var (_, blocked) = ResticSettings.ParseEnv("RESTIC_PASSWORD_COMMAND=curl evil | sh");
        Assert.NotNull(blocked);
        Assert.DoesNotContain("curl", blocked);

        var (_, malformed) = ResticSettings.ParseEnv("AWS_ACCESS_KEY_ID=ok\nsuper-secret-without-equals");
        Assert.Equal("Line 2 is not a NAME=value line.", malformed);
        Assert.DoesNotContain("secret", malformed);
    }

    // ---- the storage refuses what it must not run ----

    [Fact]
    public void The_storage_checks_the_repository_and_the_keys_again()
    {
        var empty = new Dictionary<string, string>();
        Assert.Throws<InvalidOperationException>(() => new ResticBackupStorage(new ResticRepoInfo("rclone:x:y", "pw", empty), "restic", "/tmp/c"));
        Assert.Throws<InvalidOperationException>(() => new ResticBackupStorage(
            new ResticRepoInfo("/repo", "pw", new Dictionary<string, string> { ["RESTIC_PASSWORD_COMMAND"] = "x" }), "restic", "/tmp/c"));
    }

    [Theory]
    [InlineData("vol_data_20261022.tar", true)]
    [InlineData("a.mdbundle.json", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a b.tar", false)]
    [InlineData("-flag", false)]
    [InlineData("", false)]
    public void A_backup_file_name_is_a_plain_name(string name, bool valid)
        => Assert.Equal(valid, ResticSettings.IsValidFileName(name));

    [Fact]
    public async Task A_bad_file_name_never_reaches_restic()
    {
        var storage = new ResticBackupStorage(new ResticRepoInfo("/repo", "pw", new Dictionary<string, string>()), "restic-does-not-exist", "/tmp/c");
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenWriteAsync("../x"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync("a b"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync("-x"));
    }

    [Fact]
    public async Task A_missing_restic_program_is_said_in_words()
    {
        var storage = new ResticBackupStorage(new ResticRepoInfo("/repo", "pw", new Dictionary<string, string>()), "restic-does-not-exist-anywhere", "/tmp/c");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.TestAsync());
        Assert.Contains("restic is not installed", ex.Message);
    }

    // ---- what restic says ----

    // A real answer of `restic snapshots --json` (0.18), one snapshot of ours and one made by something else.
    private const string SnapshotsJson = """
        [{"time":"2026-10-22T07:30:53.662103746Z","tree":"8a1c","paths":["/vol_a_20261022.tar"],"hostname":"matdock","username":"root","tags":["mdfile:vol_a_20261022.tar"],
          "program_version":"restic 0.18.0","summary":{"backup_start":"2026-10-22T07:30:53Z","total_files_processed":1,"total_bytes_processed":15},
          "id":"63217beb07d0f31b679b422f147a908c74cb15680f8713bba7867806637ac25b","short_id":"63217beb"},
         {"time":"2026-10-22T07:30:50Z","paths":["/etc"],"hostname":"matdock","tags":["manual"],"id":"ffff","short_id":"ffff"},
         {"time":"2026-10-21T01:00:00Z","paths":["/b.tar"],"hostname":"matdock","tags":["x","mdfile:b.tar"],"id":"aaaa","short_id":"aaaa"}]
        """;

    [Fact]
    public void Snapshots_are_read_oldest_first_and_only_ours()
    {
        var list = ResticBackupStorage.ParseSnapshots(SnapshotsJson);
        Assert.Equal(new[] { "b.tar", "vol_a_20261022.tar" }, list.Select(s => s.FileName));
        Assert.Equal(15, list[1].Bytes);
        Assert.Equal(0, list[0].Bytes);                       // an older restic has no summary: size unknown
        Assert.Equal(new DateTime(2026, 10, 22, 7, 30, 53, DateTimeKind.Utc).Date, list[1].TimeUtc!.Value.Date);
        Assert.Equal("63217beb07d0f31b679b422f147a908c74cb15680f8713bba7867806637ac25b", list[1].Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Unreadable_snapshot_output_is_an_empty_list(string json)
        => Assert.Empty(ResticBackupStorage.ParseSnapshots(json));

    [Theory]
    [InlineData(10, "", "does not exist")]
    [InlineData(11, "", "locked")]
    [InlineData(12, "", "Wrong repository password")]
    [InlineData(1, "Fatal: unable to open repo\nmore\nlines\nand more", "Fatal: unable to open repo more lines")]
    [InlineData(1, "", "exited with code 1")]
    public void Exit_codes_are_said_in_words(int exit, string stderr, string expected)
        => Assert.Contains(expected, ResticBackupStorage.Describe(exit, stderr));

    // ---- the target service ----

    private static BackupTargetService Service(TestDatabase db)
        => new(db.Context, new FakeSecretProtector(), new FakeBackupStorageFactory());

    private static BackupTargetInput ResticInput(string name = "Offsite") => new()
    {
        Name = name,
        Type = BackupTargetType.Restic,
        ResticRepository = "s3:s3.amazonaws.com/bucket/matdock",
        ResticPassword = "correct horse",
        ResticEnv = "AWS_ACCESS_KEY_ID=AKIA\nAWS_SECRET_ACCESS_KEY=shh",
    };

    [Fact]
    public async Task A_restic_target_stores_its_password_and_keys_encrypted()
    {
        using var db = new TestDatabase();
        var t = await Service(db).CreateAsync(ResticInput());

        Assert.Equal(BackupTargetType.Restic, t.Type);
        Assert.Equal("s3:s3.amazonaws.com/bucket/matdock", t.ResticRepository);
        Assert.Equal("enc:correct horse", t.EncryptedResticPassword);
        Assert.StartsWith("enc:AWS_ACCESS_KEY_ID=AKIA", t.EncryptedResticEnv);
        Assert.Null(t.SmbHost);
        Assert.Null(t.EncryptedSmbPassword);
    }

    [Fact]
    public async Task Blank_password_and_keys_keep_what_is_stored_on_edit()
    {
        using var db = new TestDatabase();
        var service = Service(db);
        var t = await service.CreateAsync(ResticInput());

        var edit = ResticInput("Renamed");
        edit.ResticPassword = null;
        edit.ResticEnv = "";
        edit.ResticRepository = "s3:s3.amazonaws.com/other";
        Assert.True(await service.UpdateAsync(t.Id, edit));

        var after = (await service.GetAsync(t.Id))!;
        Assert.Equal("Renamed", after.Name);
        Assert.Equal("s3:s3.amazonaws.com/other", after.ResticRepository);
        Assert.Equal("enc:correct horse", after.EncryptedResticPassword);
        Assert.StartsWith("enc:AWS_ACCESS_KEY_ID=AKIA", after.EncryptedResticEnv);
    }

    [Fact]
    public async Task A_new_password_replaces_the_stored_one()
    {
        using var db = new TestDatabase();
        var service = Service(db);
        var t = await service.CreateAsync(ResticInput());

        var edit = ResticInput();
        edit.ResticPassword = "new password";
        await service.UpdateAsync(t.Id, edit);
        Assert.Equal("enc:new password", (await service.GetAsync(t.Id))!.EncryptedResticPassword);
    }

    [Theory]
    [InlineData("", "pw", "AWS_ACCESS_KEY_ID=a")]                          // no repository
    [InlineData("rclone:x:y", "pw", "")]                                     // not allowed
    [InlineData("s3:b/p", "", "")]                                           // no password on create
    [InlineData("s3:b/p", "pw", "RESTIC_PASSWORD_COMMAND=curl x|sh")]      // a command variable
    public async Task An_invalid_restic_target_is_not_saved(string repo, string password, string env)
    {
        using var db = new TestDatabase();
        var input = ResticInput();
        input.ResticRepository = repo;
        input.ResticPassword = password;
        input.ResticEnv = env;

        Assert.NotNull(BackupTargetService.ValidateInput(input));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).CreateAsync(input));
        Assert.Empty(await Service(db).GetAllAsync());
    }

    [Fact]
    public async Task The_type_of_an_existing_target_cannot_change()
    {
        using var db = new TestDatabase();
        var service = Service(db);
        var smb = await service.CreateAsync(new BackupTargetInput { Name = "NAS", Type = BackupTargetType.Smb, SmbHost = "nas", SmbShare = "b" });

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(smb.Id, ResticInput()));
    }

    [Fact]
    public void A_smb_target_still_needs_a_host_and_a_share()
    {
        Assert.NotNull(BackupTargetService.ValidateInput(new BackupTargetInput { Name = "x", Type = BackupTargetType.Smb, SmbShare = "s" }));
        Assert.NotNull(BackupTargetService.ValidateInput(new BackupTargetInput { Name = "x", Type = BackupTargetType.Smb, SmbHost = "h" }));
        Assert.Null(BackupTargetService.ValidateInput(new BackupTargetInput { Name = "x", Type = BackupTargetType.Smb, SmbHost = "h", SmbShare = "s" }));
    }

    [Fact]
    public void The_factory_builds_a_restic_storage_from_a_target_and_refuses_a_bad_one()
    {
        var factory = new BackupStorageFactory(new AppPaths("/tmp/matdock-test"), new FakeSecretProtector(), Options.Create(new MatDockOptions()));
        var good = new BackupTarget
        {
            Type = BackupTargetType.Restic,
            ResticRepository = "/mnt/repo",
            EncryptedResticPassword = "enc:pw",
            EncryptedResticEnv = "enc:AWS_ACCESS_KEY_ID=a",
        };
        Assert.IsType<ResticBackupStorage>(factory.Create(good));

        var bad = new BackupTarget { Type = BackupTargetType.Restic, ResticRepository = "/mnt/repo", EncryptedResticPassword = "enc:pw", EncryptedResticEnv = "enc:RESTIC_PASSWORD_COMMAND=x" };
        Assert.Throws<InvalidOperationException>(() => factory.Create(bad));
    }
}

/// <summary>A fact that runs when <c>MATDOCK_TEST_RESTIC</c> names a restic program (the CI image and the Linux run have one), otherwise it is skipped.</summary>
public sealed class ResticFactAttribute : FactAttribute
{
    public static string? Exe => Environment.GetEnvironmentVariable("MATDOCK_TEST_RESTIC");

    public ResticFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Exe))
        {
            Skip = "Set MATDOCK_TEST_RESTIC to a restic program to run the real restic tests.";
        }
    }
}

/// <summary>Real restic, a repository in a temporary folder: write, list, read, delete and maintain backup files as snapshots.</summary>
public sealed class ResticRoundtripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matdock-restic-" + Guid.NewGuid().ToString("N"));

    public ResticRoundtripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a temp folder */ }
    }

    private ResticBackupStorage Storage(string password = "pw")
        => new(new ResticRepoInfo(Path.Combine(_dir, "repo"), password, new Dictionary<string, string>()),
            ResticFactAttribute.Exe!, Path.Combine(_dir, "cache"));

    private static async Task WriteAsync(IBackupStorage storage, string name, byte[] bytes)
    {
        await using var s = await storage.OpenWriteAsync(name);
        // In pieces, like a tar stream arriving from a host.
        for (var i = 0; i < bytes.Length; i += 64 * 1024)
        {
            await s.WriteAsync(bytes.AsMemory(i, Math.Min(64 * 1024, bytes.Length - i)));
        }
    }

    private static async Task<byte[]> ReadAsync(IBackupStorage storage, string name)
    {
        await using var s = await storage.OpenReadAsync(name);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    [ResticFact]
    public async Task A_file_roundtrips_through_a_snapshot_and_is_listed()
    {
        var storage = Storage();
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123); // several chunks, not a multiple of the buffer

        await WriteAsync(storage, "vol_data_20261022.tar", data);

        var listed = Assert.Single(await storage.ListAsync());
        Assert.Equal("vol_data_20261022.tar", listed.FileName);
        Assert.Equal(data.Length, listed.SizeBytes);
        Assert.NotNull(listed.ModifiedUtc);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(await ReadAsync(storage, "vol_data_20261022.tar")));
    }

    [ResticFact]
    public async Task Deleting_a_file_forgets_only_its_snapshot()
    {
        var storage = Storage();
        await WriteAsync(storage, "a.tar", new byte[] { 1, 2, 3 });
        await WriteAsync(storage, "b.tar", new byte[] { 4, 5, 6, 7 });

        await storage.DeleteAsync("a.tar");

        Assert.Equal(new[] { "b.tar" }, (await storage.ListAsync()).Select(f => f.FileName));
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync("a.tar"));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, await ReadAsync(storage, "b.tar"));
        await storage.DeleteAsync("a.tar"); // already gone: not an error
    }

    [ResticFact]
    public async Task An_empty_or_new_repository_lists_nothing_and_test_creates_it()
    {
        var storage = Storage();
        Assert.Empty(await storage.ListAsync()); // does not exist yet
        await storage.TestAsync();               // initializes it
        Assert.True(File.Exists(Path.Combine(_dir, "repo", "config")));
        Assert.Empty(await storage.ListAsync());
    }

    [ResticFact]
    public async Task A_wrong_password_is_said_in_words()
    {
        await Storage("right").TestAsync();

        var ex = await Assert.ThrowsAsync<IOException>(() => Storage("wrong").TestAsync());
        Assert.Contains("Wrong repository password", ex.Message);
        await Assert.ThrowsAsync<IOException>(() => Storage("wrong").ListAsync());
    }

    [ResticFact]
    public async Task Maintenance_prunes_and_checks_a_repository_with_forgotten_snapshots()
    {
        var storage = Storage();
        await WriteAsync(storage, "a.tar", RandomNumberGenerator.GetBytes(200_000));
        await WriteAsync(storage, "b.tar", RandomNumberGenerator.GetBytes(100_000));
        await storage.DeleteAsync("a.tar");

        var (ok, summary) = await storage.MaintainAsync();

        Assert.True(ok, summary);
        Assert.Contains("no errors", summary);
        Assert.Equal(new[] { "b.tar" }, (await storage.ListAsync()).Select(f => f.FileName)); // what stays is intact
    }

    [ResticFact]
    public async Task A_snapshot_of_another_host_or_without_our_tag_is_left_alone()
    {
        var storage = Storage();
        await storage.TestAsync();
        await WriteAsync(storage, "ours.tar", new byte[] { 9 });

        // Something else puts a snapshot into the same repository (a manual backup of a folder).
        var foreign = new System.Diagnostics.ProcessStartInfo(ResticFactAttribute.Exe!, new[] { "backup", _dir + Path.DirectorySeparatorChar + "cache", "--quiet", "--host", "other" })
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreign.Environment["RESTIC_REPOSITORY"] = Path.Combine(_dir, "repo");
        foreign.Environment["RESTIC_PASSWORD"] = "pw";
        foreign.Environment["RESTIC_CACHE_DIR"] = Path.Combine(_dir, "cache2");
        using (var p = System.Diagnostics.Process.Start(foreign)!)
        {
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode is 0 or 3, p.StandardError.ReadToEnd());
        }

        Assert.Equal(new[] { "ours.tar" }, (await storage.ListAsync()).Select(f => f.FileName));
    }
}
