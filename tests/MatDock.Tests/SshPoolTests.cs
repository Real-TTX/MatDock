using MatDock.Core.Entities;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// SSH sessions share kept-open connections (<see cref="SshConnectionPool"/>), so a host is not logged into for every command. The real ones need an SSH
/// server: <c>MATDOCK_TEST_SSH=host:port:user:password</c> (skipped without it).
/// </summary>
public class SshPoolTests
{
    private static SshConnectionSettings Settings(string host = "h", string password = "p", string? hostKey = "SHA256:x")
        => new() { ConnectionType = ConnectionType.Ssh, Host = host, Port = 22, Username = "u", Password = password, HostKeyFingerprint = hostKey };

    [Fact]
    public void A_connection_is_shared_only_by_the_same_endpoint_credentials_and_host_key()
    {
        Assert.Equal(SshConnectionPool.Key(Settings()), SshConnectionPool.Key(Settings()));
        Assert.NotEqual(SshConnectionPool.Key(Settings()), SshConnectionPool.Key(Settings(password: "other")));
        Assert.NotEqual(SshConnectionPool.Key(Settings()), SshConnectionPool.Key(Settings(host: "h2")));
        Assert.NotEqual(SshConnectionPool.Key(Settings()), SshConnectionPool.Key(Settings(hostKey: "SHA256:y")));
        Assert.DoesNotContain("p", SshConnectionPool.Key(Settings(password: "secret-password")).Split('|')); // only a hash of the secrets
    }

    private static SshConnectionSettings? Real(string? hostKey)
    {
        var spec = Environment.GetEnvironmentVariable("MATDOCK_TEST_SSH");
        if (string.IsNullOrEmpty(spec))
        {
            return null;
        }

        var parts = spec.Split(':', 4);
        return new SshConnectionSettings
        {
            ConnectionType = ConnectionType.Ssh, Host = parts[0], Port = int.Parse(parts[1]), Username = parts[2], Password = parts[3],
            AuthType = AuthType.Password, HostKeyFingerprint = hostKey, TimeoutSeconds = 15,
        };
    }

    /// <summary>The process of the SSH connection a command runs under (the parent of its shell): one per login.</summary>
    private static async Task<string> ConnectionOfAsync(IHostSessionFactory hosts, SshConnectionSettings settings)
    {
        using var session = hosts.Create(settings);
        await session.ConnectAsync(CancellationToken.None);
        using var cmd = session.CreateCommand("ps -o ppid= -p $$");
        return cmd.Execute().Trim();
    }

    [Fact]
    public async Task Against_a_real_server_many_sessions_share_one_login_and_a_first_contact_gets_its_own()
    {
        if (Real(null) is not { } first)
        {
            return;
        }

        using var hosts = new HostSessionFactory(new SshNetClientFactory());

        // First contact (no saved host key): its own connection, and the key it shows is seen.
        var a = await ConnectionOfAsync(hosts, first);
        Assert.NotNull(first.HostKey.Seen);
        var b = await ConnectionOfAsync(hosts, Real(null)!);
        Assert.NotEqual(a, b);

        // With the saved key: twenty sessions one after another, one login.
        var known = first.HostKey.Seen;
        var sequential = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            sequential.Add(await ConnectionOfAsync(hosts, Real(known)!));
        }

        Assert.Single(sequential);

        // Twelve at once: a connection serves four sessions at a time, so three connections (the pool's limit per host).
        var parallel = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ConnectionOfAsync(hosts, Real(known)!))));
        Assert.InRange(parallel.Distinct().Count(), 1, SshConnectionPool.ConnectionsPerHost);
        Assert.All(parallel, p => Assert.Matches("^[0-9]+$", p));

        // A wrong password is not served by the open connection of the right one.
        var wrong = Real(known)! is var w ? new SshConnectionSettings
        {
            ConnectionType = ConnectionType.Ssh, Host = w.Host, Port = w.Port, Username = w.Username, Password = "wrong", AuthType = AuthType.Password,
            HostKeyFingerprint = known, TimeoutSeconds = 15,
        } : null;
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectionOfAsync(hosts, wrong!));
    }
}
