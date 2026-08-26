using MatDock.Core.Docker;
using MatDock.Core.Ssh;

namespace MatDock.Core.Environments;

/// <summary>
/// Pure connectivity operations against a remote Docker environment described by
/// <see cref="SshConnectionSettings"/>. It has no knowledge of the database or of secret storage,
/// which keeps it usable both for saved environments and for still-unsaved form input.
/// </summary>
public interface IEnvironmentConnectionService
{
    Task<DockerConnectionResult> TestConnectionAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Full detail (incl. driver options / remote share) of one volume, or null if it does not exist.</summary>
    Task<DockerVolumeDetail?> InspectVolumeAsync(SshConnectionSettings settings, string volume, CancellationToken cancellationToken = default);

    Task<HostStats> GetHostStatsAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);
}
