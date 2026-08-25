using MatDock.Core.Backups;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests;

public class BackupTargetServiceTests
{
    private static BackupTargetService NewService(TestDatabase db)
        => new(db.Context, new FakeSecretProtector(), new FakeBackupStorageFactory());

    [Fact]
    public async Task Cannot_delete_target_that_still_has_backups()
    {
        using var db = new TestDatabase();
        var service = NewService(db);

        var target = await service.CreateAsync(new BackupTargetInput
        {
            Name = "NAS", Type = BackupTargetType.Smb, SmbHost = "nas", SmbShare = "backups", SmbPassword = "pw"
        });

        db.Context.VolumeBackups.Add(new VolumeBackup
        {
            VolumeName = "data", FileName = "f.tar", BackupTargetId = target.Id, BackupTargetName = "NAS", SizeBytes = 10
        });
        await db.Context.SaveChangesAsync();

        var (blocked, message) = await service.DeleteAsync(target.Id);
        Assert.False(blocked);
        Assert.Contains("verwendet", message);

        // After the backup is gone, deletion succeeds.
        var backup = db.Context.VolumeBackups.First();
        db.Context.VolumeBackups.Remove(backup);
        await db.Context.SaveChangesAsync();

        var (ok, _) = await service.DeleteAsync(target.Id);
        Assert.True(ok);
    }

    [Fact]
    public async Task Only_one_default_target()
    {
        using var db = new TestDatabase();
        var service = NewService(db);

        var a = await service.CreateAsync(new BackupTargetInput { Name = "A", Type = BackupTargetType.Smb, SmbHost = "a", SmbShare = "s", IsDefault = true });
        var b = await service.CreateAsync(new BackupTargetInput { Name = "B", Type = BackupTargetType.Smb, SmbHost = "b", SmbShare = "s", IsDefault = true });

        var all = await service.GetAllAsync();
        Assert.Single(all, t => t.IsDefault);
        Assert.Equal(b.Id, (await service.GetDefaultAsync())!.Id);
    }

    [Fact]
    public async Task Smb_password_is_encrypted_and_kept_on_blank_update()
    {
        using var db = new TestDatabase();
        var service = NewService(db);

        var t = await service.CreateAsync(new BackupTargetInput { Name = "N", Type = BackupTargetType.Smb, SmbHost = "h", SmbShare = "s", SmbPassword = "secret" });
        Assert.Equal("enc:secret", t.EncryptedSmbPassword);

        await service.UpdateAsync(t.Id, new BackupTargetInput { Name = "N2", Type = BackupTargetType.Smb, SmbHost = "h", SmbShare = "s", SmbPassword = null });
        var reloaded = await service.GetAsync(t.Id);
        Assert.Equal("N2", reloaded!.Name);
        Assert.Equal("enc:secret", reloaded.EncryptedSmbPassword);
    }
}
