using System.Text.Json;
using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Images;
using MatDock.Core.Registries;
using MatDock.Core.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Updates;

/// <summary>
/// Detects image updates for a stack (local digest vs. registry digest) and runs a manual update
/// (optional pre-update backup, then <c>compose pull</c> + <c>up -d</c>); the schedule action
/// <see cref="Schedules.UpdateStacksAction"/> runs both on a timer.
/// </summary>
public sealed class UpdateService
{
    /// <summary>How many update runs are kept per stack; older ones are removed.</summary>
    public const int KeepRuns = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ContainerService _containers;
    private readonly IEnvironmentConnectionService _connection;
    private readonly EnvironmentService _environments;
    private readonly RegistryService _registries;
    private readonly StackService _stacks;
    private readonly BundleBackupService _bundle;
    private readonly UpdateCheckCache _cache;
    private readonly MatDockDbContext _db;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        ContainerService containers,
        IEnvironmentConnectionService connection,
        EnvironmentService environments,
        RegistryService registries,
        StackService stacks,
        BundleBackupService bundle,
        UpdateCheckCache cache,
        MatDockDbContext db,
        ILogger<UpdateService> logger)
    {
        _containers = containers;
        _connection = connection;
        _environments = environments;
        _registries = registries;
        _stacks = stacks;
        _bundle = bundle;
        _cache = cache;
        _db = db;
        _logger = logger;
    }

    /// <summary>The newest update runs of a stack, newest first.</summary>
    public async Task<IReadOnlyList<StackUpdateRun>> GetHistoryAsync(long stackId, int take = 10, CancellationToken ct = default)
        => await _db.StackUpdateRuns.AsNoTracking()
            .Where(r => r.StackId == stackId)
            .OrderByDescending(r => r.CreateDate).ThenByDescending(r => r.Id)
            .Take(Math.Clamp(take, 1, KeepRuns))
            .ToListAsync(ct);

    /// <summary>The services whose image changed in a run (empty when none, or the record has no detail).</summary>
    public static IReadOnlyList<UpdateChange> ChangesOf(StackUpdateRun run)
    {
        if (string.IsNullOrWhiteSpace(run.ChangesJson))
        {
            return Array.Empty<UpdateChange>();
        }

        try { return JsonSerializer.Deserialize<List<UpdateChange>>(run.ChangesJson, Json) ?? []; }
        catch (JsonException) { return Array.Empty<UpdateChange>(); }
    }

    /// <summary>
    /// Checks each image of a running stack for a newer registry digest (moving tags). Pinned services are reported as pinned and never
    /// as an update. With <paramref name="newerTags"/> a plain version tag (<c>1.2.3</c>) also gets a hint when the registry has a newer one.
    /// </summary>
    public async Task<StackUpdateReport> CheckStackAsync(DockerEnvironment env, string projectName, CancellationToken ct = default, bool newerTags = false)
    {
        var pinned = (await _stacks.GetByNameAsync(env.Id, projectName, ct)) is { } managed
            ? StackService.PinnedOf(managed).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

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
        var registryDigests = new Dictionary<string, string?>(StringComparer.Ordinal); // one registry call per image, not per service
        foreach (var c in containers)
        {
            var image = c.Image?.Trim() ?? string.Empty;
            var service = string.IsNullOrEmpty(c.Service) ? c.Name : c.Service;
            if (image.Length == 0 || image == "<none>:<none>" || !seen.Add($"{service}|{image}"))
            {
                continue;
            }

            var iref = ImageRef.TryParse(image);
            if (pinned.Contains(service))
            {
                statuses.Add(new ImageUpdateStatus(service, image, localDigests.GetValueOrDefault(image), null, false, "Pinned: updates skip it.", Pinned: true));
                continue;
            }
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
            if (!registryDigests.TryGetValue(image, out var registryDigest))
            {
                registryDigest = await _registries.GetDigestForImageAsync(iref.Host, iref.Repository, iref.Tag, ct);
                registryDigests[image] = registryDigest;
            }

            var available = !string.IsNullOrEmpty(localDigest) && !string.IsNullOrEmpty(registryDigest)
                            && !string.Equals(localDigest, registryDigest, StringComparison.OrdinalIgnoreCase);
            string? note = registryDigest is null
                ? "Registry unreachable or requires credentials."
                : string.IsNullOrEmpty(localDigest) ? "Local digest unknown (pull once)." : null;

            // A plain version tag (1.2.3): say so when the registry has a newer one. Informational — nothing changes by itself.
            var newer = newerTags && TagVersion.TryParse(iref.Tag) is not null
                ? TagVersion.Newest(iref.Tag, await TagsAsync(iref, ct))
                : null;

            statuses.Add(new ImageUpdateStatus(service, image, localDigest, registryDigest, available, note, NewerTag: newer));
        }

        var report = new StackUpdateReport(statuses, null);
        _cache.Set(env.Id, projectName, report);
        return report;
    }

    /// <summary>
    /// What the rule of a stack would update now: per service its strategy decides (a moved tag, the newest version of the series, or nothing). A service that is
    /// pinned or has no newer image is not in the plan.
    /// </summary>
    public async Task<StackUpdatePlan> PlanStackAsync(DockerEnvironment env, Stack stack, EffectiveUpdatePolicy policy, CancellationToken ct = default)
    {
        var report = await CheckStackAsync(env, stack.Name, ct);
        if (report.Error is not null)
        {
            return new StackUpdatePlan(Array.Empty<PlannedUpdate>(), report.Error);
        }

        var items = new List<PlannedUpdate>();
        foreach (var image in report.Images)
        {
            var strategy = policy.StrategyOf(image.Service);
            if (image.Pinned || strategy == UpdateStrategy.Pinned)
            {
                continue;
            }

            if (strategy == UpdateStrategy.Tag)
            {
                if (image.UpdateAvailable)
                {
                    items.Add(new PlannedUpdate(image.Service, image.Image, ShortDigest(image.RegistryDigest), PlannedKind.Digest));
                }

                continue;
            }

            if (ImageRef.TryParse(image.Image) is not { IsDigestPinned: false } iref)
            {
                continue;
            }

            var target = TagVersion.Pick(iref.Tag, await TagsAsync(iref, ct), sameMajor: strategy == UpdateStrategy.Minor);
            if (target is not null && ImageRef.WithTag(image.Image, target) is { } targetImage && ImageCommands.IsValidImageRef(targetImage))
            {
                items.Add(new PlannedUpdate(image.Service, image.Image, targetImage, PlannedKind.Version));
            }
        }

        var plan = new StackUpdatePlan(items, null);
        _cache.SetPlan(env.Id, stack.Name, plan);
        return plan;
    }

    private static string ShortDigest(string? digest)
        => string.IsNullOrEmpty(digest) ? "new" : (digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : digest) is var h && h.Length > 12 ? "sha256:" + h[..12] : digest;

    private async Task<IReadOnlyList<string>> TagsAsync(ImageRef iref, CancellationToken ct)
    {
        try { return await _registries.ListTagsForImageAsync(iref.Host, iref.Repository, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Listing the tags of {Repository} failed.", iref.Repository);
            return Array.Empty<string>();
        }
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
    /// <param name="trigger">Who started it, for the history: "manual", "schedule" or "api".</param>
    public async Task<StackUpdateOutcome> RunUpdateAsync(long stackId, bool preBackup, UpdateSafety? safety, CancellationToken ct = default, Action<string>? log = null, string trigger = "manual")
    {
        var trace = new RunTrace();
        var outcome = await RunUpdateCoreAsync(stackId, preBackup, safety, ct, log, trace);
        await RecordRunAsync(stackId, trigger, outcome, trace);
        return outcome;
    }

    /// <summary>What a run learns on the way, for its history entry.</summary>
    private sealed class RunTrace
    {
        public string? StackName { get; set; }
        public long EnvironmentId { get; set; }
        public IReadOnlyList<(string Service, string Reference, string ImageId)>? Before { get; set; }
        public IReadOnlyList<(string Service, string Reference, string ImageId)>? After { get; set; }
        public bool RolledBack { get; set; }
    }

    private async Task<StackUpdateOutcome> RunUpdateCoreAsync(long stackId, bool preBackup, UpdateSafety? safety, CancellationToken ct, Action<string>? log, RunTrace trace)
    {
        var stack = await _stacks.GetAsync(stackId, ct);
        if (stack is null)
        {
            return StackUpdateOutcome.NotStartedWith("Stack not found.");
        }

        var env = await _environments.GetAsync(stack.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return StackUpdateOutcome.NotStartedWith("Environment not available (disabled or deleted).");
        }

        trace.StackName = stack.Name;
        trace.EnvironmentId = env.Id;
        trace.Before = await ImagesAsync(env, stack.Name, ct);

        if (preBackup)
        {
            log?.Invoke("==> Bundle backup before the update");
            var (backupOk, backupMsg, _) = await _bundle.CreateBundleAsync(env, stack.Name, target: null, stopContainers: false,
                StackBackupKind.PreUpdate, "Before update", ct: ct);
            if (!backupOk)
            {
                // Never update without the safety net the user asked for.
                return StackUpdateOutcome.NotStartedWith($"Pre-update backup failed, update aborted: {backupMsg}");
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
        trace.After = await ImagesAsync(env, stack.Name, ct);
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
            return new StackUpdateOutcome(false, $"{output}\nUpdated, but: {reason}", false, Deployed: true);
        }

        log?.Invoke("==> Rolling back to the images from before the update");
        trace.RolledBack = true;
        var rolledBack = await RollbackAsync(env, stack.Id, kept, trace, ct);
        await ForgetKeptAsync(env, kept, ct);
        return new StackUpdateOutcome(false, $"{output}\nUpdated, but: {reason} {rolledBack}", false, RolledBack: true);
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

    /// <summary>
    /// A rollback deploys the stack again, and the deploy reads the image overrides of the stack: one that brought the new image would bring it back. So the services whose
    /// image reference the update changed get the reference they ran before (an override, or none when that is the compose file's own image).
    /// </summary>
    private async Task<IReadOnlyList<string>> RestoreOverridesAsync(long stackId, RunTrace trace, CancellationToken ct)
    {
        var errors = new List<string>();
        if (trace.Before is null || trace.After is null)
        {
            return errors;
        }

        var after = trace.After.GroupBy(a => a.Service, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Reference, StringComparer.Ordinal);
        foreach (var (service, reference, _) in trace.Before.DistinctBy(b => b.Service))
        {
            if (!after.TryGetValue(service, out var now) || string.Equals(now, reference, StringComparison.Ordinal) || !Stacks.StackCommands.IsValidServiceName(service))
            {
                continue;
            }

            var (ok, message) = await _stacks.SetImageOverrideAsync(stackId, service, reference, ct);
            if (!ok)
            {
                errors.Add($"{service}: {message}");
            }
        }

        return errors;
    }

    /// <summary>Puts every service's old image back under its tag and deploys the stack again (no pull).</summary>
    private async Task<string> RollbackAsync(DockerEnvironment env, long stackId, IReadOnlyList<(string Reference, string Kept)> kept, RunTrace trace, CancellationToken ct)
    {
        var errors = new List<string>();
        errors.AddRange(await RestoreOverridesAsync(stackId, trace, ct));
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

    private async Task<IReadOnlyList<(string Service, string Reference, string ImageId)>?> ImagesAsync(DockerEnvironment env, string project, CancellationToken ct)
    {
        try { return await _containers.ImagesOfProjectAsync(env, project, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reading the images of {Project} failed.", project);
            return null;
        }
    }

    /// <summary>The services whose image id differs between two readings (a service that is new or gone is not a change here).</summary>
    public static IReadOnlyList<UpdateChange> DiffChanges(
        IReadOnlyList<(string Service, string Reference, string ImageId)>? before,
        IReadOnlyList<(string Service, string Reference, string ImageId)>? after)
    {
        if (before is null || after is null)
        {
            return Array.Empty<UpdateChange>();
        }

        static string Short(string id) => (id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id) is var h && h.Length > 12 ? h[..12] : h;
        var old = before.GroupBy(b => b.Service, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return after
            .Where(a => old.TryGetValue(a.Service, out var b) && !string.Equals(b.ImageId, a.ImageId, StringComparison.Ordinal))
            .Select(a => new UpdateChange(a.Service, a.Reference, Short(old[a.Service].ImageId), Short(a.ImageId)))
            .DistinctBy(c => c.Service)
            .ToList();
    }

    /// <summary>Writes the history entry of a run and drops the oldest beyond <see cref="KeepRuns"/>. Never throws: a failing history must not fail an update.</summary>
    private async Task RecordRunAsync(long stackId, string trigger, StackUpdateOutcome outcome, RunTrace trace)
    {
        if (trace.StackName is null)
        {
            return; // the stack or its environment was not found: nothing ran
        }

        try
        {
            var changes = DiffChanges(trace.Before, trace.After);
            var message = outcome.Message.Trim();
            _db.StackUpdateRuns.Add(new StackUpdateRun
            {
                StackId = stackId,
                StackName = trace.StackName,
                EnvironmentId = trace.EnvironmentId,
                Trigger = trigger is "schedule" or "api" or "auto" ? trigger : "manual",
                Outcome = outcome.Ok
                    ? (outcome.Changed ? UpdateRunOutcome.Updated : UpdateRunOutcome.NoChange)
                    : (trace.RolledBack ? UpdateRunOutcome.RolledBack : UpdateRunOutcome.Failed),
                Message = message.Length > 900 ? "…" + message[^899..] : message, // the end of the compose output is what matters
                ChangesJson = changes.Count == 0 ? null : JsonSerializer.Serialize(changes, Json),
            });
            await _db.SaveChangesAsync();

            var stale = await _db.StackUpdateRuns
                .Where(r => r.StackId == stackId)
                .OrderByDescending(r => r.CreateDate).ThenByDescending(r => r.Id)
                .Skip(KeepRuns)
                .ToListAsync();
            if (stale.Count > 0)
            {
                _db.StackUpdateRuns.RemoveRange(stale);
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording the update run of {Stack} failed.", trace.StackName);
        }
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
