using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Registries;
using MatDock.Core.Stacks;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Updates;

/// <summary>
/// Detects image updates for a stack (local digest vs. registry digest) and runs a manual update
/// (optional pre-update backup, then <c>compose pull</c> + <c>up -d</c>). Health-gate/rollback and
/// auto-update come in later phases.
/// </summary>
public sealed class UpdateService
{
    private readonly ContainerService _containers;
    private readonly IEnvironmentConnectionService _connection;
    private readonly EnvironmentService _environments;
    private readonly RegistryService _registries;
    private readonly StackService _stacks;
    private readonly BundleBackupService _bundle;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        ContainerService containers,
        IEnvironmentConnectionService connection,
        EnvironmentService environments,
        RegistryService registries,
        StackService stacks,
        BundleBackupService bundle,
        ILogger<UpdateService> logger)
    {
        _containers = containers;
        _connection = connection;
        _environments = environments;
        _registries = registries;
        _stacks = stacks;
        _bundle = bundle;
        _logger = logger;
    }

    /// <summary>Checks each image of a running stack for a newer registry digest (moving tags).</summary>
    public async Task<StackUpdateReport> CheckStackAsync(DockerEnvironment env, string projectName, CancellationToken ct = default)
    {
        IReadOnlyList<DockerContainer> containers;
        Dictionary<string, string?> localDigests;
        try
        {
            containers = await _containers.ListByProjectAsync(env, projectName, ct);
            var settings = _environments.BuildSettings(env);
            var images = await _connection.ListImagesAsync(settings, ct);
            localDigests = images
                .GroupBy(i => $"{i.Repository}:{i.Tag}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Digest, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return StackUpdateReport.Failed(ex.Message);
        }

        var statuses = new List<ImageUpdateStatus>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in containers)
        {
            var image = c.Image?.Trim() ?? string.Empty;
            if (image.Length == 0 || image == "<none>:<none>" || !seen.Add(image))
            {
                continue;
            }

            var service = string.IsNullOrEmpty(c.Service) ? c.Name : c.Service;
            var iref = ImageRef.TryParse(image);
            if (iref is null)
            {
                statuses.Add(new ImageUpdateStatus(service, image, null, null, false, "Could not parse image reference."));
                continue;
            }
            if (iref.IsDigestPinned)
            {
                statuses.Add(new ImageUpdateStatus(service, image, iref.Digest, iref.Digest, false, "Pinned to a digest."));
                continue;
            }

            var localDigest = localDigests.GetValueOrDefault(image);
            var registryDigest = await _registries.GetDigestForImageAsync(iref.Host, iref.Repository, iref.Tag, ct);
            var available = !string.IsNullOrEmpty(localDigest) && !string.IsNullOrEmpty(registryDigest)
                            && !string.Equals(localDigest, registryDigest, StringComparison.OrdinalIgnoreCase);
            string? note = registryDigest is null
                ? "Registry unreachable or requires credentials."
                : string.IsNullOrEmpty(localDigest) ? "Local digest unknown (pull once)." : null;

            statuses.Add(new ImageUpdateStatus(service, image, localDigest, registryDigest, available, note));
        }

        return new StackUpdateReport(statuses, null);
    }

    /// <summary>Runs a manual update of a managed stack: optional pre-update backup, then pull + redeploy.</summary>
    public async Task<(bool Ok, string Message)> UpdateStackAsync(long stackId, bool preBackup, CancellationToken ct = default)
    {
        var stack = await _stacks.GetAsync(stackId, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        var env = await _environments.GetAsync(stack.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return (false, "Environment not available (disabled or deleted).");
        }

        if (preBackup)
        {
            var (backupOk, backupMsg) = await _bundle.BackupStackAsync(env, stack.Name, target: null, stopContainers: false, ct);
            if (!backupOk)
            {
                // Never update without the safety net the user asked for.
                return (false, $"Pre-update backup failed, update aborted: {backupMsg}");
            }
        }

        var (ok, output) = await _stacks.UpdateAsync(stackId, ct);
        return (ok, output);
    }
}
