using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>Trust on first use for the SSH host key of an environment.</summary>
public class HostKeyTests
{
    private static (EnvironmentService Service, FakeConnectionService Connection) Service(TestDatabase db)
    {
        var connection = new FakeConnectionService();
        return (new EnvironmentService(db.Context, new FakeSecretProtector(), connection, Options.Create(new MatDockOptions())), connection);
    }

    private static EnvironmentInput Input(string host = "h", string user = "root") => new()
    {
        Name = "P", Host = host, Username = user, AuthType = AuthType.Password, Password = "pw",
    };

    [Theory]
    [InlineData(null, "SHA256:abc", true)]
    [InlineData("SHA256:abc", "SHA256:abc", true)]
    [InlineData("SHA256:abc=", "SHA256:abc", true)]
    [InlineData("SHA256:abc", "SHA256:xyz", false)]
    public void A_saved_key_must_match(string? expected, string seen, bool trusted)
        => Assert.Equal(trusted, HostKeyCheck.Trusts(expected, seen));

    [Fact]
    public void Fingerprints_read_like_ssh_keygen()
        => Assert.Equal("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU", HostKeyCheck.Format("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="));

    [Fact]
    public async Task The_first_key_is_saved_and_then_expected()
    {
        using var db = new TestDatabase();
        var (service, connection) = Service(db);
        var env = await service.CreateAsync(Input());
        Assert.Null(service.BuildSettings(env).HostKeyFingerprint);

        connection.HostKey = "SHA256:first";
        await service.TestAndPersistAsync(env.Id);
        connection.HostKey = "SHA256:second";
        await service.TestAndPersistAsync(env.Id); // a later answer never replaces the saved key

        var saved = (await service.GetAsync(env.Id))!;
        Assert.Equal("SHA256:first", saved.HostKeyFingerprint);
        Assert.Equal("SHA256:first", service.BuildSettings(saved).HostKeyFingerprint);

        // The test of the form expects it for the same host only.
        Assert.Equal("SHA256:first", (await service.BuildTestSettingsAsync(Input(), env.Id)).HostKeyFingerprint);
        Assert.Null((await service.BuildTestSettingsAsync(Input(host: "other"), env.Id)).HostKeyFingerprint);
    }

    [Fact]
    public async Task Another_host_or_forgetting_clears_the_key()
    {
        using var db = new TestDatabase();
        var (service, _) = Service(db);
        var env = await service.CreateAsync(Input());
        await service.RememberHostKeyAsync(env.Id, "SHA256:k");
        await service.RememberHostKeyAsync(env.Id, "SHA256:other"); // only when none is saved
        Assert.Equal("SHA256:k", (await service.GetAsync(env.Id))!.HostKeyFingerprint);

        await service.UpdateAsync(env.Id, Input()); // same host: kept
        Assert.Equal("SHA256:k", (await service.GetAsync(env.Id))!.HostKeyFingerprint);
        await service.UpdateAsync(env.Id, Input(host: "new-host"));
        Assert.Null((await service.GetAsync(env.Id))!.HostKeyFingerprint);

        await service.RememberHostKeyAsync(env.Id, "SHA256:n");
        Assert.True(await service.ForgetHostKeyAsync(env.Id));
        Assert.Null((await service.GetAsync(env.Id))!.HostKeyFingerprint);
    }

    /// <summary>Against a real SSH server, when MATDOCK_SSH_TEST (host:port:user:password) is set: the right key is
    /// trusted and reported, a wrong one refused.</summary>
    [Fact]
    public void A_real_server_is_refused_with_another_key()
    {
        var spec = Environment.GetEnvironmentVariable("MATDOCK_SSH_TEST");
        if (string.IsNullOrEmpty(spec))
        {
            return;
        }

        var p = spec.Split(':');
        SshConnectionSettings Settings(string? key) => new()
        {
            Host = p[0], Port = int.Parse(p[1]), Username = p[2], AuthType = AuthType.Password, Password = p[3], HostKeyFingerprint = key,
        };

        var factory = new SshNetClientFactory();
        var first = Settings(null);
        using (var client = factory.Create(first))
        {
            client.Connect();
            Assert.StartsWith("SHA256:", first.HostKey.Seen);
            Assert.False(first.HostKey.Rejected);
        }

        var wrong = Settings("SHA256:not-the-key");
        using (var client = factory.Create(wrong))
        {
            Assert.ThrowsAny<Exception>(() => client.Connect());
            Assert.True(wrong.HostKey.Rejected);
        }

        var right = Settings(first.HostKey.Seen);
        using (var client = factory.Create(right))
        {
            client.Connect();
            Assert.True(client.IsConnected);
        }
    }
}
