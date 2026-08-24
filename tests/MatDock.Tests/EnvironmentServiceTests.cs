using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

public class EnvironmentServiceTests
{
    private static EnvironmentService NewService(TestDatabase db, FakeConnectionService? conn = null)
        => new(db.Context, new FakeSecretProtector(), conn ?? new FakeConnectionService(),
               Options.Create(new MatDockOptions { SshTimeoutSeconds = 15 }));

    [Fact]
    public async Task Create_encrypts_password_and_leaves_key_null()
    {
        using var db = new TestDatabase();
        var service = NewService(db);

        var env = await service.CreateAsync(new EnvironmentInput
        {
            Name = "Prod",
            Host = "example.com",
            Username = "root",
            AuthType = AuthType.Password,
            Password = "hunter2"
        });

        Assert.Equal("enc:hunter2", env.EncryptedPassword);
        Assert.Null(env.EncryptedPrivateKey);
    }

    [Fact]
    public async Task Update_with_blank_password_keeps_existing_secret()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var env = await service.CreateAsync(new EnvironmentInput
        {
            Name = "Prod", Host = "h", Username = "root", AuthType = AuthType.Password, Password = "keepme"
        });

        await service.UpdateAsync(env.Id, new EnvironmentInput
        {
            Name = "Prod-Renamed", Host = "h", Username = "root", AuthType = AuthType.Password, Password = null
        });

        var reloaded = await service.GetAsync(env.Id);
        Assert.Equal("Prod-Renamed", reloaded!.Name);
        Assert.Equal("enc:keepme", reloaded.EncryptedPassword);
    }

    [Fact]
    public async Task Switching_auth_type_clears_the_other_secret()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var env = await service.CreateAsync(new EnvironmentInput
        {
            Name = "P", Host = "h", Username = "root", AuthType = AuthType.Password, Password = "pw"
        });

        await service.UpdateAsync(env.Id, new EnvironmentInput
        {
            Name = "P", Host = "h", Username = "root", AuthType = AuthType.PrivateKey, PrivateKeyPem = "----KEY----"
        });

        var reloaded = await service.GetAsync(env.Id);
        Assert.Null(reloaded!.EncryptedPassword);
        Assert.Equal("enc:----KEY----", reloaded.EncryptedPrivateKey);
    }

    [Fact]
    public async Task BuildSettings_decrypts_secret()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var env = await service.CreateAsync(new EnvironmentInput
        {
            Name = "P", Host = "h", Username = "root", AuthType = AuthType.Password, Password = "pw"
        });

        var settings = service.BuildSettings(env);
        Assert.Equal("pw", settings.Password);
        Assert.Equal("root", settings.Username);
        Assert.Equal(15, settings.TimeoutSeconds);
    }

    [Fact]
    public async Task BuildTestSettings_falls_back_to_stored_secret_when_blank()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var env = await service.CreateAsync(new EnvironmentInput
        {
            Name = "P", Host = "h", Username = "root", AuthType = AuthType.Password, Password = "stored"
        });

        var settings = await service.BuildTestSettingsAsync(new EnvironmentInput
        {
            Host = "h2", Username = "root", AuthType = AuthType.Password, Password = null
        }, env.Id);

        Assert.Equal("h2", settings.Host);          // edited scalar honoured
        Assert.Equal("stored", settings.Password);  // secret pulled from storage
    }

    [Fact]
    public async Task TestAndPersist_updates_status()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var env = await service.CreateAsync(new EnvironmentInput
        {
            Name = "P", Host = "h", Username = "root", AuthType = AuthType.Password, Password = "pw"
        });

        var result = await service.TestAndPersistAsync(env.Id);

        Assert.True(result.Success);
        var reloaded = await service.GetAsync(env.Id);
        Assert.Equal(EnvironmentStatus.Online, reloaded!.Status);
        Assert.Equal("27.0", reloaded.DockerVersion);
        Assert.NotNull(reloaded.LastCheckedAt);
    }
}
