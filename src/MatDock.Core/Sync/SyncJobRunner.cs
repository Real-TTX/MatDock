using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Git;
using MatDock.Core.Schedules;
using MatDock.Core.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Sync;

/// <summary>
/// Executes one <see cref="SyncJob"/>: clones the repo once, then creates/updates and deploys a managed
/// <see cref="Stack"/> for every selected compose file. Honors change-detection (OnGitChange), image
/// pulling and pruning of removed/deselected stacks. Returns a short human-readable summary.
/// </summary>
public sealed class SyncJobRunner
{
    private readonly MatDockDbContext _db;
    private readonly StackService _stacks;
    private readonly GitCredentialService _gitCredentials;
    private readonly GitRepositoryService _gitRepo;
    private readonly IScheduleEventBus _events;
    private readonly ILogger<SyncJobRunner> _logger;

    public SyncJobRunner(
        MatDockDbContext db,
        StackService stacks,
        GitCredentialService gitCredentials,
        GitRepositoryService gitRepo,
        IScheduleEventBus events,
        ILogger<SyncJobRunner> logger)
    {
        _db = db;
        _stacks = stacks;
        _gitCredentials = gitCredentials;
        _gitRepo = gitRepo;
        _events = events;
        _logger = logger;
    }

    // Serializes concurrent runs of the SAME job (webhook + scheduler + manual) so they can't race on
    // the unique (Name, Environment) stack index or on prune. Shared across DI scopes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> JobGates = new();

    public async Task<string> RunAsync(SyncJob job, bool force, CancellationToken ct = default)
    {
        var gate = JobGates.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            return "Already running — skipped.";
        }

        try
        {
            return await RunCoreAsync(job, force, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> RunCoreAsync(SyncJob job, bool force, CancellationToken ct)
    {
        var items = await _db.SyncJobItems.Where(i => i.SyncJobId == job.Id).ToListAsync(ct);
        var active = items.Where(i => i.Enabled).ToList();
        // The set of stacks that SHOULD exist = the enabled items (by name+env), independent of whether a
        // given deploy succeeds this run. Prune compares against this, never against "deployed OK".
        var activeKeys = new HashSet<(string, long)>(active.Select(i => (i.StackName, i.EnvironmentId)));

        GitCredentialSecret? secret = null;
        if (job.GitCredentialId is > 0)
        {
            try
            {
                secret = await _gitCredentials.ResolveAsync(job.GitCredentialId.Value, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync job {Name}: git credential decrypt failed.", job.Name);
                return "Git credentials could not be decrypted.";
            }

            if (secret is null)
            {
                return "The assigned Git credentials were not found.";
            }
        }

        int ok = 0, fail = 0, pruned = 0, skipped = 0;

        // Prune deselected / disabled / removed-from-config stacks first. This needs no clone, so it also
        // works on "no changes" ticks (OnGitChange) — deselecting an item prunes it without a new commit.
        if (job.PruneRemoved)
        {
            var jobStacks = await _db.Stacks.Where(s => s.SyncJobId == job.Id).ToListAsync(ct);
            foreach (var s in jobStacks.Where(s => !activeKeys.Contains((s.Name, s.EnvironmentId))))
            {
                if (await PruneStackAsync(job, s, ct))
                {
                    pruned++;
                }
            }
        }

        // Change detection for the DEPLOY part only (prune above already ran).
        if (job.UpdateMode == SyncUpdateMode.OnGitChange && !force && !string.IsNullOrEmpty(job.LastCommitSha))
        {
            var remoteSha = await _gitRepo.GetRemoteHeadShaAsync(job.GitRepoUrl, job.GitReference, secret, ct);
            if (remoteSha is not null && string.Equals(remoteSha, job.LastCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                if (pruned > 0)
                {
                    await _db.SaveChangesAsync(ct);
                }
                return $"No changes ({Short(remoteSha)})" + (pruned > 0 ? $", {pruned} pruned" : string.Empty) + ".";
            }
        }

        GitScanResult scan;
        try
        {
            scan = await _gitRepo.CloneAndScanAsync(job.GitRepoUrl, job.GitReference, secret, ct);
        }
        catch (GitOperationException ex)
        {
            return $"Git error: {ex.Message}";
        }

        var now = DateTime.UtcNow;
        try
        {
            foreach (var item in active)
            {
                var relFsPath = item.ComposePath.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(Path.Combine(scan.WorkDir, relFsPath)))
                {
                    // Compose no longer in the repo. Don't create a phantom stack; prune the existing one
                    // (if any) when pruning is on, otherwise surface it as a failure.
                    item.LastStatus = "Compose missing in repo";
                    if (job.PruneRemoved)
                    {
                        var existing = await _db.Stacks.FirstOrDefaultAsync(
                            s => s.SyncJobId == job.Id && s.Name == item.StackName && s.EnvironmentId == item.EnvironmentId, ct);
                        if (existing is not null && await PruneStackAsync(job, existing, ct))
                        {
                            pruned++;
                            continue;
                        }
                    }
                    fail++;
                    continue;
                }

                var (stack, conflict) = await UpsertStackAsync(job, item, ct);
                if (conflict is not null)
                {
                    item.LastStatus = conflict;
                    skipped++;
                    continue;
                }

                var (deployOk, output) = await _stacks.DeployFromWorkDirAsync(stack!, scan.WorkDir, job.PullImages, ct);
                item.StackId = stack!.Id;
                item.LastStatus = deployOk ? "Deployed" : Trunc(output);
                if (deployOk)
                {
                    item.LastDeployedAt = now;
                    ok++;
                }
                else
                {
                    fail++;
                }
            }

            // Advance the change-detection marker ONLY when everything that should deploy actually did,
            // so OnGitChange retries a failed commit on the next scheduled/webhook tick.
            if (fail == 0 && skipped == 0)
            {
                job.LastCommitSha = scan.CommitSha;
            }

            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            GitRepositoryService.TryDelete(scan.WorkDir);
        }

        if (fail > 0) { await _events.PublishAsync(ScheduleEvent.SyncFailed, null, job.Name, ct); }

        var parts = new List<string> { $"{ok} deployed" };
        if (fail > 0) { parts.Add($"{fail} failed"); }
        if (skipped > 0) { parts.Add($"{skipped} skipped"); }
        if (pruned > 0) { parts.Add($"{pruned} pruned"); }
        var sha = scan.CommitSha is null ? string.Empty : $" ({Short(scan.CommitSha)})";
        return string.Join(", ", parts) + sha;
    }

    /// <summary>Tears a stack down and removes its record — but only DELETES when the down actually
    /// succeeded, so a failed <c>compose down</c> never leaves running containers without a DB record.</summary>
    private async Task<bool> PruneStackAsync(SyncJob job, Stack stack, CancellationToken ct)
    {
        try
        {
            var (downOk, _) = await _stacks.DownAsync(stack.Id, ct);
            if (!downOk)
            {
                _logger.LogWarning("Sync job {Name}: keeping stack {Stack} (compose down failed).", job.Name, stack.Name);
                return false;
            }

            await _stacks.DeleteAsync(stack.Id, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync job {Name}: pruning stack {Stack} failed.", job.Name, stack.Name);
            return false;
        }
    }

    /// <summary>Finds or creates the managed stack for an item, refreshing its git source from the job.
    /// Returns a conflict message instead when the name belongs to a manual stack or another sync job.</summary>
    private async Task<(Stack? Stack, string? Conflict)> UpsertStackAsync(SyncJob job, SyncJobItem item, CancellationToken ct)
    {
        Stack? stack = null;
        if (item.StackId is > 0)
        {
            stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == item.StackId, ct);
        }
        stack ??= await _db.Stacks.FirstOrDefaultAsync(
            s => s.Name == item.StackName && s.EnvironmentId == item.EnvironmentId, ct);

        if (stack is not null && stack.SyncJobId != job.Id)
        {
            return (null, stack.SyncJobId is null
                ? $"Name \"{item.StackName}\" already used by a manual stack – skipped."
                : "Name already managed by another sync job – skipped.");
        }

        if (stack is null)
        {
            stack = new Stack();
            _db.Stacks.Add(stack);
        }

        stack.Name = item.StackName;
        stack.EnvironmentId = item.EnvironmentId;
        stack.GitRepoUrl = job.GitRepoUrl;
        stack.GitReference = job.GitReference;
        stack.GitComposePath = item.ComposePath;
        stack.GitCredentialId = job.GitCredentialId;
        stack.SyncJobId = job.Id;
        await _db.SaveChangesAsync(ct); // ensure Id is assigned before deploy/link
        return (stack, null);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string Trunc(string s)
    {
        s = s.Trim();
        return s.Length <= 300 ? s : s[..300];
    }
}
