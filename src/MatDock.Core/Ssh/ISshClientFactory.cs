using System.Text;
using MatDock.Core.Entities;
using Renci.SshNet;

namespace MatDock.Core.Ssh;

/// <summary>Builds (but does not connect) configured <see cref="SshClient"/> instances.</summary>
public interface ISshClientFactory
{
    SshClient Create(SshConnectionSettings settings);
}

/// <summary>SSH.NET-backed factory that translates <see cref="SshConnectionSettings"/> into a client.</summary>
public sealed class SshNetClientFactory : ISshClientFactory
{
    public SshClient Create(SshConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var authMethod = settings.AuthType switch
        {
            AuthType.Password => BuildPasswordAuth(settings),
            AuthType.PrivateKey => BuildPrivateKeyAuth(settings),
            _ => throw new NotSupportedException($"Unsupported auth type: {settings.AuthType}")
        };

        var connectionInfo = new ConnectionInfo(settings.Host, settings.Port, settings.Username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))
        };

        return new SshClient(connectionInfo);
    }

    private static AuthenticationMethod BuildPasswordAuth(SshConnectionSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Password))
        {
            throw new InvalidOperationException("Password authentication selected but no password was provided.");
        }

        return new PasswordAuthenticationMethod(settings.Username, settings.Password);
    }

    private static AuthenticationMethod BuildPrivateKeyAuth(SshConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PrivateKeyPem))
        {
            throw new InvalidOperationException("Private-key authentication selected but no private key was provided.");
        }

        using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKeyPem));
        var keyFile = string.IsNullOrEmpty(settings.PrivateKeyPassphrase)
            ? new PrivateKeyFile(keyStream)
            : new PrivateKeyFile(keyStream, settings.PrivateKeyPassphrase);

        return new PrivateKeyAuthenticationMethod(settings.Username, keyFile);
    }
}
