using MatDock.Core.Ssh;

namespace MatDock.Core.Execution;

/// <summary>Creates an SSH or Local <see cref="IHostSession"/> depending on the environment's connection type.</summary>
public sealed class HostSessionFactory : IHostSessionFactory
{
    private readonly ISshClientFactory _sshClientFactory;

    public HostSessionFactory(ISshClientFactory sshClientFactory) => _sshClientFactory = sshClientFactory;

    public IHostSession Create(SshConnectionSettings settings)
        => settings.IsLocal
            ? new LocalHostSession()
            : new SshHostSession(_sshClientFactory.Create(settings));
}
