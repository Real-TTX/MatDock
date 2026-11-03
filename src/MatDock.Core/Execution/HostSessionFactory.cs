using MatDock.Core.Configuration;
using MatDock.Core.Ssh;

namespace MatDock.Core.Execution;

/// <summary>
/// Creates an SSH, Local or bridge <see cref="IHostSession"/> depending on the environment's connection type. SSH sessions share kept-open connections
/// (<see cref="SshConnectionPool"/>), so a host is not logged into for every command.
/// </summary>
public sealed class HostSessionFactory : IHostSessionFactory, IDisposable
{
    private readonly SshConnectionPool _ssh;
    private readonly string? _localStacksPath;

    /// <param name="sshClientFactory">Opens the SSH connections of remote environments.</param>
    /// <param name="paths">When given, local commands keep their stack files below the data directory
    /// (<see cref="AppPaths.StacksPath"/>) instead of the (non-persistent) home directory of the process.</param>
    public HostSessionFactory(ISshClientFactory sshClientFactory, AppPaths? paths = null)
    {
        _ssh = new SshConnectionPool(sshClientFactory);
        _localStacksPath = paths?.StacksPath;
    }

    public IHostSession Create(SshConnectionSettings settings)
        => settings.IsBridge ? new BridgeHostSession(settings) : CreateDirect(settings);

    private IHostSession CreateDirect(SshConnectionSettings settings)
        => settings.IsTcp
            ? LocalHostSession.ForDockerApi(_localStacksPath, settings)
            : settings.IsLocal
                ? new LocalHostSession(_localStacksPath)
                : _ssh.Create(settings);

    public void Dispose() => _ssh.Dispose();
}
