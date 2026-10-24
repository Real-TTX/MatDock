using System.Net;
using System.Security.Cryptography;
using System.Text;
using MatDock.Core.Backups;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Schedules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// The backup of MatDock itself: the database and the key ring in one file encrypted with a passphrase, a restore that waits for the next start and keeps
/// what it replaces, and a schedule that writes one to a target and keeps the newest.
/// </summary>
public sealed class SelfBackupTests : IClassFixture<SelfBackupTests.Site>
{
    private const string Passphrase = "correct horse battery";

    public sealed class Site : IDisposable
    {
        public MatDockFactory App { get; } = new();

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public SelfBackupTests(Site site) => _site = site;

    private async Task WithAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = _site.App.Services.CreateScope();
        await work(scope.ServiceProvider);
    }

    [Fact]
    public void The_format_opens_only_with_the_passphrase_and_notices_a_change()
    {
        var plain = Encoding.UTF8.GetBytes("database and keys");
        var sealedData = SelfBackupFormat.Encrypt(plain, Passphrase);

        Assert.Equal("MDSB1"u8.ToArray(), sealedData[..5]);
        Assert.DoesNotContain("database", Encoding.UTF8.GetString(sealedData));
        Assert.Equal(plain, SelfBackupFormat.Decrypt(sealedData, Passphrase));
        Assert.ThrowsAny<CryptographicException>(() => SelfBackupFormat.Decrypt(sealedData, "another passphrase"));

        sealedData[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => SelfBackupFormat.Decrypt(sealedData, Passphrase));
        Assert.Throws<InvalidDataException>(() => SelfBackupFormat.Decrypt("PK\u0003\u0004 a zip file"u8.ToArray(), Passphrase));
    }

    [Fact]
    public async Task A_backup_holds_the_database_and_the_keys_and_a_restore_waits_for_the_next_start()
    {
        await WithAsync(async sp =>
        {
            var service = sp.GetRequiredService<SelfBackupService>();
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("short"));

            // The key ring is written with the first secret.
            sp.GetRequiredService<MatDock.Core.Security.ISecretProtector>().Protect("a secret");
            var data = await service.CreateAsync(Passphrase);
            var refused = await service.PrepareRestoreAsync(new MemoryStream(data), "wrong passphrase!");
            Assert.False(refused.Ok);
            Assert.False(service.HasPendingRestore);

            var (ok, message) = await service.PrepareRestoreAsync(new MemoryStream(data), Passphrase);
            Assert.True(ok, message);
            Assert.True(service.HasPendingRestore);

            var pending = SelfBackupRestore.PendingPath(sp.GetRequiredService<AppPaths>());
            Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(pending, "matdock.db")).AsSpan(0, 15)));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(pending, "keys"), "key-*.xml"));

            service.CancelPendingRestore();
            Assert.False(service.HasPendingRestore);
        });
    }

    [Fact]
    public async Task A_file_that_is_no_backup_is_refused()
    {
        await WithAsync(async sp =>
        {
            var service = sp.GetRequiredService<SelfBackupService>();
            Assert.False((await service.PrepareRestoreAsync(new MemoryStream("hello"u8.ToArray()), Passphrase)).Ok);

            // Encrypted right, but without a database in it.
            var empty = SelfBackupFormat.Encrypt(new byte[1024], Passphrase);
            Assert.False((await service.PrepareRestoreAsync(new MemoryStream(empty), Passphrase)).Ok);
            Assert.False(service.HasPendingRestore);
        });
    }

    [Fact]
    public void The_start_swaps_the_restore_in_and_keeps_what_it_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "matdock-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            Assert.Null(SelfBackupRestore.ApplyPending(paths));

            File.WriteAllText(paths.DatabasePath, "old database");
            File.WriteAllText(paths.DatabasePath + "-wal", "old wal");
            File.WriteAllText(Path.Combine(paths.KeysPath, "key-old.xml"), "old key");
            var pending = SelfBackupRestore.PendingPath(paths);
            Directory.CreateDirectory(Path.Combine(pending, "keys"));
            File.WriteAllText(Path.Combine(pending, "matdock.db"), "new database");
            File.WriteAllText(Path.Combine(pending, "keys", "key-new.xml"), "new key");

            var message = SelfBackupRestore.ApplyPending(paths);

            Assert.NotNull(message);
            Assert.Equal("new database", File.ReadAllText(paths.DatabasePath));
            Assert.False(File.Exists(paths.DatabasePath + "-wal"));
            Assert.Equal(["key-new.xml"], Directory.GetFiles(paths.KeysPath).Select(Path.GetFileName));
            Assert.False(Directory.Exists(pending));

            var kept = Assert.Single(Directory.GetDirectories(root, "before-restore-*"));
            Assert.Equal("old database", File.ReadAllText(Path.Combine(kept, "matdock.db")));
            Assert.Equal("old wal", File.ReadAllText(Path.Combine(kept, "matdock.db-wal")));
            Assert.Equal("old key", File.ReadAllText(Path.Combine(kept, "dataprotection-keys", "key-old.xml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_schedule_needs_a_passphrase_keeps_it_hidden_and_writes_the_newest_backups()
    {
        await WithAsync(async sp =>
        {
            var schedules = sp.GetRequiredService<ScheduleService>();
            var input = new ScheduleInput { Name = "MatDock backup", Action = ScheduleAction.SelfBackup, Cron = "30 3 * * *", RetentionCount = 2 };

            Assert.False((await schedules.CreateAsync(input)).Ok);
            input.SelfBackupPassphrase = "short";
            Assert.False((await schedules.CreateAsync(input)).Ok);

            input.SelfBackupPassphrase = Passphrase;
            var (ok, message, id) = await schedules.CreateAsync(input);
            Assert.True(ok, message);
            var stored = (await schedules.GetAsync(id))!.OptionsJson!;
            Assert.DoesNotContain(Passphrase, stored);
            Assert.NotNull(ScheduleOptions.Parse(stored).SelfBackupPassphrase);

            // A save with an empty field keeps the passphrase.
            input.SelfBackupPassphrase = null;
            Assert.True((await schedules.UpdateAsync(id, input)).Ok);
            Assert.Equal(ScheduleOptions.Parse(stored).SelfBackupPassphrase, ScheduleOptions.Parse((await schedules.GetAsync(id))!.OptionsJson).SelfBackupPassphrase);

            var folder = sp.GetRequiredService<AppPaths>().BackupsPath;
            foreach (var old in new[] { "20200101-000000", "20200102-000000" })
            {
                File.WriteAllBytes(Path.Combine(folder, $"{SelfBackupService.FilePrefix}{old}{SelfBackupService.FileExtension}"), [1]);
            }
            File.WriteAllBytes(Path.Combine(folder, "another-archive.tar.gz"), [1]);

            var summary = await schedules.RunNowAsync(id);
            Assert.Contains("written", summary);

            var backups = Directory.GetFiles(folder, SelfBackupService.FilePrefix + "*").Select(Path.GetFileName).Order().ToList();
            Assert.Equal(2, backups.Count);
            Assert.Equal($"{SelfBackupService.FilePrefix}20200102-000000{SelfBackupService.FileExtension}", backups[0]);
            Assert.True(File.Exists(Path.Combine(folder, "another-archive.tar.gz")));

            var written = File.ReadAllBytes(Path.Combine(folder, backups[1]!));
            Assert.True((await sp.GetRequiredService<SelfBackupService>().PrepareRestoreAsync(new MemoryStream(written), Passphrase)).Ok);
            sp.GetRequiredService<SelfBackupService>().CancelPendingRestore();
        });
    }

    [Fact]
    public async Task The_page_downloads_a_backup_for_an_admin_only()
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", login)).StatusCode);

        var page = await (await browser.GetAsync("/Settings/SelfBackup")).Content.ReadAsStringAsync();
        Assert.Contains("handler=Download", page);
        Assert.Contains("handler=Restore", page);
        Assert.Contains("/Schedules/Edit?action=SelfBackup", page);

        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page), ["passphrase"] = Passphrase, ["repeat"] = "something else" };
        var mismatch = await browser.PostAsync("/Settings/SelfBackup?handler=Download", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, mismatch.StatusCode);

        form["repeat"] = Passphrase;
        var download = await browser.PostAsync("/Settings/SelfBackup?handler=Download", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.EndsWith(".mdsb", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var data = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal("MDSB1"u8.ToArray(), data[..5]);

        var user = _site.App.Browser();
        var userLogin = await (await user.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        SignIn.AddUser(_site.App, "sbuser", "UserPass123!");
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(user, "/Account/Login", userLogin, "sbuser", "UserPass123!")).StatusCode);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(await user.GetAsync("/Settings/SelfBackup")));
    }

    [Fact]
    public async Task The_schedule_editor_offers_the_action_with_its_defaults()
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", login)).StatusCode);

        var html = await (await browser.GetAsync("/Schedules/Edit?action=SelfBackup")).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"SelfBackup\" selected=\"selected\">", html);
        Assert.Contains("value=\"MatDock backup\"", html);
        Assert.Contains("Input_SelfBackupPassphrase", html);
    }
}
