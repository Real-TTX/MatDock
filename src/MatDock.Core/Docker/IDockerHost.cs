using Docker.DotNet;

namespace MatDock.Core.Docker;

/// <summary>
/// A live connection to a remote Docker engine reached over SSH. Owns the underlying SSH connection,
/// the local proxy and the Docker.DotNet client; dispose it to release all of them.
/// </summary>
public interface IDockerHost : IAsyncDisposable
{
    /// <summary>The Docker Engine API client, tunnelled over SSH.</summary>
    IDockerClient Client { get; }
}

/// <summary>Opens <see cref="IDockerHost"/> connections from SSH settings.</summary>
public interface IDockerHostFactory
{
    Task<IDockerHost> CreateAsync(MatDock.Core.Ssh.SshConnectionSettings settings, CancellationToken cancellationToken = default);
}
