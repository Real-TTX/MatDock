using MatDock.Core.Configuration;
using MatDock.Core.Ssh;

namespace MatDock.Core.Execution;

/// <summary>Creates an SSH or Local <see cref="IHostSession"/> depending on the environment's connection type.</summary>
public sealed class HostSessionFactory : IHostSessionFactory
{
    private readonly ISshClientFactory _sshClientFactory;
    private readonly string? _localStacksPath;

    /// <param name="sshClientFactory">Opens the SSH connections of remote environments.</param>
    /// <param name="paths">When given, local commands keep their stack files below the data directory
    /// (<see cref="AppPaths.StacksPath"/>) instead of the (non-persistent) home directory of the process.</param>
    public HostSessionFactory(ISshClientFactory sshClientFactory, AppPaths? paths = null)
    {
        _sshClientFactory = sshClientFactory;
        _localStacksPath = paths?.StacksPath;
    }

    public IHostSession Create(SshConnectionSettings settings)
        => settings.IsTcp
            ? LocalHostSession.ForDockerApi(_localStacksPath, settings)
            : settings.IsLocal
                ? new LocalHostSession(_localStacksPath)
                : new SshHostSession(_sshClientFactory.Create(settings));
}
