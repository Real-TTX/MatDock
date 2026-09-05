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

    /// <summary>
    /// Lists volumes together with the set of names that are unused (Docker's "dangling" filter — not
    /// referenced by any container). Both are fetched over a single connection.
    /// </summary>
    Task<(IReadOnlyList<DockerVolume> Volumes, IReadOnlyCollection<string> UnusedNames)> ListVolumesWithUsageAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Full detail (incl. driver options / remote share) of one volume, or null if it does not exist.</summary>
    Task<DockerVolumeDetail?> InspectVolumeAsync(SshConnectionSettings settings, string volume, CancellationToken cancellationToken = default);

    /// <summary>Creates a named volume (docker volume create). Returns an actionable message on failure.</summary>
    Task<(bool Ok, string Message)> CreateVolumeAsync(SshConnectionSettings settings, string name, CancellationToken cancellationToken = default);

    /// <summary>Creates a volume with an explicit driver and <c>--opt</c> options (e.g. NFS/CIFS mounts).</summary>
    Task<(bool Ok, string Message)> CreateVolumeAsync(SshConnectionSettings settings, string name, string? driver, IReadOnlyList<(string Key, string Value)> options, CancellationToken cancellationToken = default);

    Task<HostStats> GetHostStatsAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);
}
