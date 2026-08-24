using MatDock.Core.Ssh;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Docker;

/// <summary>Default <see cref="IDockerHostFactory"/>: connects over SSH, then starts the proxy.</summary>
public sealed class SshDockerHostFactory : IDockerHostFactory
{
    private readonly ISshClientFactory _sshClientFactory;
    private readonly ILogger<SshDockerHostFactory> _logger;

    public SshDockerHostFactory(ISshClientFactory sshClientFactory, ILogger<SshDockerHostFactory> logger)
    {
        _sshClientFactory = sshClientFactory;
        _logger = logger;
    }

    public async Task<IDockerHost> CreateAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ssh = _sshClientFactory.Create(settings);
        try
        {
            await ssh.ConnectAsync(cancellationToken);
        }
        catch
        {
            ssh.Dispose();
            throw;
        }

        var dockerTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds));
        try
        {
            return SshDockerHost.Start(ssh, dockerTimeout, _logger);
        }
        catch
        {
            try { if (ssh.IsConnected) { ssh.Disconnect(); } } catch { /* best effort */ }
            ssh.Dispose();
            throw;
        }
    }
}
