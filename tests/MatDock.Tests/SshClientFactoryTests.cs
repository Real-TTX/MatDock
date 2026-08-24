using MatDock.Core.Entities;
using MatDock.Core.Ssh;
using Xunit;

namespace MatDock.Tests;

public class SshClientFactoryTests
{
    private readonly SshNetClientFactory _factory = new();

    [Fact]
    public void Creates_client_with_connection_info_for_password_auth()
    {
        using var client = _factory.Create(new SshConnectionSettings
        {
            Host = "example.com",
            Port = 2222,
            Username = "root",
            AuthType = AuthType.Password,
            Password = "pw",
            TimeoutSeconds = 10
        });

        Assert.Equal("example.com", client.ConnectionInfo.Host);
        Assert.Equal(2222, client.ConnectionInfo.Port);
        Assert.Equal("root", client.ConnectionInfo.Username);
    }

    [Fact]
    public void Throws_when_password_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _factory.Create(new SshConnectionSettings
        {
            Host = "h", Username = "u", AuthType = AuthType.Password, Password = null
        }));
        Assert.Contains("Password", ex.Message);
    }

    [Fact]
    public void Throws_when_private_key_missing()
    {
        Assert.Throws<InvalidOperationException>(() => _factory.Create(new SshConnectionSettings
        {
            Host = "h", Username = "u", AuthType = AuthType.PrivateKey, PrivateKeyPem = null
        }));
    }
}
