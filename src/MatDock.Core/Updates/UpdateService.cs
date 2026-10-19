using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Images;
using MatDock.Core.Registries;
using MatDock.Core.Stacks;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Updates;

/// <summary>
/// Detects image updates for a stack (local digest vs. registry digest) and runs a manual update
/// (optional pre-update backup, then <c>compose pull</c> + <c>up -d</c>); the schedule action
/// <see cref="Schedules.UpdateStacksAction"/> runs both on a timer.
/// </summary>
public sealed class UpdateService
{
    private readonly ContainerService _containers;
    private readonly IEnvironmentConnectionService _connection;
    private readonly EnvironmentService _environments;
    private readonly RegistryService _registries;
    private readonly StackService _stacks;
    private readonly BundleBackupService _bundle;
    private readonly UpdateCheckCache _cache;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        ContainerService containers,
        IEnvironmentConnectionService connection,
        EnvironmentService environments,
        RegistryService registries,
        StackService stacks,
        BundleBackupService bundle,
        UpdateCheckCache cache,
        ILogger<UpdateService> logger)
    {
        _containers = containers;
        _connection = connection;
        _environments = environments;
        _registries = registries;
        _stacks = stacks;
        _bundle = bundle;
        _cache = cache;
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

        var report = new StackUpdateReport(statuses, null);
        _cache.Set(env.Id, projectName, report);
        return report;
    }

    /// <summary>Runs a manual update of a managed stack: optional pre-update backup, then pull + redeploy.</summary>
    public async Task<(bool Ok, string Message)> UpdateStackAsync(long stackId, bool preBackup, CancellationToken ct = default, UpdateSafety? safety = null)
    {
        var outcome = await RunUpdateAsync(stackId, preBackup, safety, ct);
        return (outcome.Ok, outcome.Message);
    }

    /// <summary>
    /// The update itself: optional pre-update bundle backup (abort when it fails), then <c>compose pull</c> +
    /// <c>up -d</c>, which re-creates only the containers whose image changed. <see cref="StackUpdateOutcome.Changed"/>
    /// tells whether any container was re-created (its id differs afterwards).
    /// </summary>
    /// <param name="stackId">The managed stack.</param>
    /// <param name="preBackup">Take a bundle backup first and abort when it fails.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="log">Optional progress lines (the backup summary).</param>
    public async Task<StackUpdateOutcome> RunUpdateAsync(long stackId, bool preBackup, CancellationToken ct = default, Action<string>? log = null)
        => await RunUpdateAsync(stackId, preBackup, null, ct, log);

    /// <summary>How long the gate waits between two looks at the containers (tests shorten it).</summary>
    internal static TimeSpan GatePoll { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The update with a health gate (<paramref name="safety"/>): after the redeploy the containers must run, with no failing or still starting
    /// health check, within <see cref="UpdateSafety.Grace"/>; otherwise, with <see cref="UpdateSafety.Rollback"/>, the images the containers ran before
    /// get their tags back and the stack is deployed again (they are still on the host: a pull keeps the old image).
    /// </summary>
    public async Task<StackUpdateOutcome> RunUpdateAsync(long stackId, bool preBackup, UpdateSafety? safety, CancellationToken ct = default, Action<string>? log = null)
    {
        var stack = await _stacks.GetAsync(stackId, ct);
        if (stack is null)
        {
            return StackUpdateOutcome.Failed("Stack not found.");
        }

        var env = await _environments.GetAsync(stack.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return StackUpdateOutcome.Failed("Environment not available (disabled or deleted).");
        }

        if (preBackup)
        {
            log?.Invoke("==> Bundle backup before the update");
            var (backupOk, backupMsg, _) = await _bundle.CreateBundleAsync(env, stack.Name, target: null, stopContainers: false,
                StackBackupKind.PreUpdate, "Before update", ct: ct);
            if (!backupOk)
            {
                // Never update without the safety net the user asked for.
                return StackUpdateOutcome.Failed($"Pre-update backup failed, update aborted: {backupMsg}");
            }

            log?.Invoke(backupMsg);
        }

        // Keep the images the containers run now under a name of their own, so a rollback finds them after the pull.
        var kept = safety is { Rollback: true } ? await KeepImagesAsync(env, stack.Name, ct) : null;
        log?.Invoke("==> docker compose pull && docker compose up -d");
        var before = await ContainerIdsAsync(env, stack.Name, ct);
        var (ok, output) = await _stacks.UpdateAsync(stackId, ct);
        if (!ok)
        {
            await ForgetKeptAsync(env, kept, ct);
            return StackUpdateOutcome.Failed(output);
        }

        _cache.Clear(env.Id, stack.Name);
        var after = await ContainerIdsAsync(env, stack.Name, ct);
        // When the containers cannot be listed, report a change rather than hide one.
        var changed = before is null || after is null || !before.SetEquals(after);
        if (safety is null || !changed)
        {
            await ForgetKeptAsync(env, kept, ct);
            return new StackUpdateOutcome(true, output, changed);
        }

        log?.Invoke($"==> Waiting up to {(int)safety.Grace.TotalSeconds} s for the containers to be up and healthy");
        var (health, reason) = await GateAsync(env, stack.Name, safety.Grace, ct);
        if (health == StackHealth.Healthy)
        {
            await ForgetKeptAsync(env, kept, ct);
            return new StackUpdateOutcome(true, output + "\nThe containers are up and healthy.", changed);
        }

        reason ??= $"The containers were not up and healthy within {(int)safety.Grace.TotalSeconds} s.";
        if (!safety.Rollback || kept is null || kept.Count == 0)
        {
            return StackUpdateOutcome.Failed($"{output}\nUpdated, but: {reason}");
        }

        log?.Invoke("==> Rolling back to the images from before the update");
        var rolledBack = await RollbackAsync(env, stack.Id, kept, ct);
        await ForgetKeptAsync(env, kept, ct);
        return StackUpdateOutcome.Failed($"{output}\nUpdated, but: {reason} {rolledBack}");
    }

    /// <summary>Tags the image of every service with its rollback name; answers (reference, rollback name) of those that worked.</summary>
    private async Task<IReadOnlyList<(string Reference, string Kept)>> KeepImagesAsync(DockerEnvironment env, string project, CancellationToken ct)
    {
        var kept = new List<(string, string)>();
        foreach (var (_, reference, imageId) in (await _containers.ImagesOfProjectAsync(env, project, ct)).DistinctBy(i => i.Reference))
        {
            if (ImageCommands.RollbackReference(reference) is not { } backup || !ImageCommands.IsValidImageRef(reference) || !ImageCommands.IsValidImageRef(backup))
            {
                continue;
            }

            try
            {
                if ((await _containers.RunAsync(env, head => ImageCommands.Tag(imageId, backup, head), ct)).Ok)
                {
                    kept.Add((reference, backup));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Keeping {Reference} for a rollback failed.", reference);
            }
        }

        return kept;
    }

    /// <summary>Removes the rollback names again (the image goes when nothing else holds it).</summary>
    private async Task ForgetKeptAsync(DockerEnvironment env, IReadOnlyList<(string Reference, string Kept)>? kept, CancellationToken ct)
    {
        foreach (var (_, backup) in kept ?? [])
        {
            try { await _containers.RunAsync(env, head => ImageCommands.Untag(backup, head), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Removing {Tag} failed.", backup); }
        }
    }

    /// <summary>Waits until the stack is healthy or failed, at most <paramref name="grace"/>; a stack that is still starting then counts as failed.</summary>
    private async Task<(StackHealth Health, string? Reason)> GateAsync(DockerEnvironment env, string project, TimeSpan grace, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + grace;
        var healthySince = (DateTime?)null;
        while (true)
        {
            var (health, reason) = UpdateHealth.Evaluate(await _containers.ListByProjectAsync(env, project, ct));
            if (health == StackHealth.Failed)
            {
                return (health, reason);
            }

            // Healthy twice in a row, so a container that crashes right after its start is caught.
            if (health == StackHealth.Healthy)
            {
                if (healthySince is not null)
                {
                    return (health, null);
                }

                healthySince = DateTime.UtcNow;
            }
            else
            {
                healthySince = null;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return (StackHealth.Failed, null);
            }

            await Task.Delay(GatePoll, ct);
        }
    }

    /// <summary>Puts every service's old image back under its tag and deploys the stack again (no pull).</summary>
    private async Task<string> RollbackAsync(DockerEnvironment env, long stackId, IReadOnlyList<(string Reference, string Kept)> kept, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (reference, backup) in kept)
        {
            try
            {
                var (ok, text) = await _containers.RunAsync(env, head => ImageCommands.Tag(backup, reference, head), ct);
                if (!ok)
                {
                    errors.Add($"{reference}: {text}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{reference}: {ex.Message}");
            }
        }

        var (deployed, output) = await _stacks.DeployAsync(stackId, ct);
        return deployed && errors.Count == 0
            ? "Rolled back to the images from before the update."
            : $"The rollback did not complete: {string.Join("; ", errors.Append(deployed ? string.Empty : output.Trim()).Where(e => e.Length > 0))}";
    }

    private async Task<HashSet<string>?> ContainerIdsAsync(DockerEnvironment env, string project, CancellationToken ct)
    {
        try
        {
            var all = await _containers.ListAsync(env, ct, includeStats: false);
            return all.Where(c => string.Equals(c.Project, project, StringComparison.Ordinal))
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing the containers of {Project} failed.", project);
            return null;
        }
    }
}
