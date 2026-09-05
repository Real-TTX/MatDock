using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Git;
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
    private readonly ILogger<SyncJobRunner> _logger;

    public SyncJobRunner(
        MatDockDbContext db,
        StackService stacks,
        GitCredentialService gitCredentials,
        GitRepositoryService gitRepo,
        ILogger<SyncJobRunner> logger)
    {
        _db = db;
        _stacks = stacks;
        _gitCredentials = gitCredentials;
        _gitRepo = gitRepo;
        _logger = logger;
    }

    public async Task<string> RunAsync(SyncJob job, bool force, CancellationToken ct = default)
    {
        var items = await _db.SyncJobItems.Where(i => i.SyncJobId == job.Id).ToListAsync(ct);
        var active = items.Where(i => i.Enabled).ToList();

        // Resolve git credentials once.
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

        // Change detection: skip a clone entirely when nothing changed since the last successful run.
        if (job.UpdateMode == SyncUpdateMode.OnGitChange && !force && !string.IsNullOrEmpty(job.LastCommitSha))
        {
            var remoteSha = await _gitRepo.GetRemoteHeadShaAsync(job.GitRepoUrl, job.GitReference, secret, ct);
            if (remoteSha is not null && string.Equals(remoteSha, job.LastCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                return $"No changes ({Short(remoteSha)}).";
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

        var deployedStackIds = new HashSet<long>();
        var now = DateTime.UtcNow;
        int ok = 0, fail = 0, pruned = 0, skipped = 0;

        try
        {
            foreach (var item in active)
            {
                var (stack, conflict) = await UpsertStackAsync(job, item, ct);
                if (conflict is not null)
                {
                    item.LastStatus = conflict;
                    skipped++;
                    continue;
                }

                // A compose that is no longer in the repo counts as "removed" (prune candidate).
                var relFsPath = item.ComposePath.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(Path.Combine(scan.WorkDir, relFsPath)))
                {
                    item.LastStatus = "Compose missing in repo";
                    fail++;
                    continue;
                }

                var (deployOk, output) = await _stacks.DeployFromWorkDirAsync(stack!, scan.WorkDir, job.PullImages, ct);
                item.StackId = stack!.Id;
                item.LastStatus = deployOk ? "Deployed" : Trunc(output);
                if (deployOk)
                {
                    item.LastDeployedAt = now;
                    ok++;
                    deployedStackIds.Add(stack.Id);
                }
                else
                {
                    fail++;
                }
            }

            // Prune stacks that belong to this job but are no longer part of the active selection.
            if (job.PruneRemoved)
            {
                var jobStacks = await _db.Stacks.Where(s => s.SyncJobId == job.Id).ToListAsync(ct);
                foreach (var s in jobStacks.Where(s => !deployedStackIds.Contains(s.Id)))
                {
                    try
                    {
                        await _stacks.DownAsync(s.Id, ct);
                        await _stacks.DeleteAsync(s.Id, ct);
                        pruned++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Sync job {Name}: pruning stack {Stack} failed.", job.Name, s.Name);
                    }
                }
            }

            job.LastCommitSha = scan.CommitSha;
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            GitRepositoryService.TryDelete(scan.WorkDir);
        }

        var parts = new List<string> { $"{ok} deployed" };
        if (fail > 0) { parts.Add($"{fail} failed"); }
        if (skipped > 0) { parts.Add($"{skipped} skipped"); }
        if (pruned > 0) { parts.Add($"{pruned} pruned"); }
        var sha = scan.CommitSha is null ? string.Empty : $" ({Short(scan.CommitSha)})";
        return string.Join(", ", parts) + sha;
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
