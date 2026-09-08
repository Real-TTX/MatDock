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

    /// <summary>
    /// Lists the unused (dangling) volumes, each classified as a local volume or a remote network share
    /// (NFS/CIFS/SMB), so prune can protect network shares by default.
    /// </summary>
    Task<IReadOnlyList<DockerUnusedVolume>> ListUnusedVolumesDetailedAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Removes the given volumes by name. Returns how many were removed.</summary>
    Task<PruneResult> RemoveVolumesAsync(SshConnectionSettings settings, IReadOnlyList<string> names, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists networks together with the names that are unused (Docker's "dangling" filter — not used by
    /// any container). Both are fetched over a single connection.
    /// </summary>
    Task<(IReadOnlyList<DockerNetwork> Networks, IReadOnlyCollection<string> UnusedNames)> ListNetworksWithUsageAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Full detail of one network, or null if it does not exist.</summary>
    Task<DockerNetworkDetail?> InspectNetworkAsync(SshConnectionSettings settings, string nameOrId, CancellationToken cancellationToken = default);

    /// <summary>Removes a single network by name or id. Returns an actionable message on failure.</summary>
    Task<(bool Ok, string Message)> RemoveNetworkAsync(SshConnectionSettings settings, string nameOrId, CancellationToken cancellationToken = default);

    /// <summary>Removes all unused (custom) networks on the host — <c>docker network prune</c>.</summary>
    Task<PruneResult> PruneNetworksAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Lists the Docker images on the host (tagged first, dangling last).</summary>
    Task<IReadOnlyList<DockerImage>> ListImagesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Removes a single image by reference or id. <paramref name="force"/> maps to <c>docker image rm -f</c>.</summary>
    Task<(bool Ok, string Message)> RemoveImageAsync(SshConnectionSettings settings, string reference, bool force, CancellationToken cancellationToken = default);

    /// <summary>Prunes dangling images, or all unused images when <paramref name="all"/> is set.</summary>
    Task<PruneResult> PruneImagesAsync(SshConnectionSettings settings, bool all, CancellationToken cancellationToken = default);
}
