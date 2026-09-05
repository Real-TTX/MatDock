using System.Security.Cryptography;
using MatDock.Core.Backups;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Git;
using MatDock.Core.Stacks;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Sync;

/// <summary>Result of scanning a repo for its compose files (for the sync-job editor).</summary>
public sealed record SyncScanResult(bool Ok, string? Error, string? CommitSha, IReadOnlyList<string> ComposeFiles);

/// <summary>CRUD for sync jobs, repo scanning, and running due / manually-triggered / webhook syncs.</summary>
public sealed class SyncJobService
{
    private readonly MatDockDbContext _db;
    private readonly GitCredentialService _gitCredentials;
    private readonly GitRepositoryService _gitRepo;
    private readonly SyncJobRunner _runner;
    private readonly ILogger<SyncJobService> _logger;

    public SyncJobService(
        MatDockDbContext db,
        GitCredentialService gitCredentials,
        GitRepositoryService gitRepo,
        SyncJobRunner runner,
        ILogger<SyncJobService> logger)
    {
        _db = db;
        _gitCredentials = gitCredentials;
        _gitRepo = gitRepo;
        _runner = runner;
        _logger = logger;
    }

    public Task<List<SyncJob>> GetAllAsync(CancellationToken ct = default)
        => _db.SyncJobs.AsNoTracking().OrderBy(j => j.Name).ToListAsync(ct);

    public Task<SyncJob?> GetAsync(long id, CancellationToken ct = default)
        => _db.SyncJobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<List<SyncJobItem>> GetItemsAsync(long jobId, CancellationToken ct = default)
        => _db.SyncJobItems.AsNoTracking().Where(i => i.SyncJobId == jobId)
            .OrderBy(i => i.ComposePath).ToListAsync(ct);

    /// <summary>Number of compose→environment items per job (for the list page).</summary>
    public async Task<Dictionary<long, int>> GetItemCountsAsync(CancellationToken ct = default)
        => await _db.SyncJobItems
            .GroupBy(i => i.SyncJobId)
            .Select(g => new { JobId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.JobId, x => x.Count, ct);

    public Task<SyncJob?> FindByWebhookTokenAsync(string token, CancellationToken ct = default)
        => _db.SyncJobs.FirstOrDefaultAsync(j => j.WebhookToken == token, ct);

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(SyncJobInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error, 0);
        }

        var job = new SyncJob { WebhookToken = GenerateToken() };
        ApplyJob(job, input);
        _db.SyncJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        foreach (var inp in input.Items)
        {
            var item = new SyncJobItem { SyncJobId = job.Id };
            ApplyItem(item, inp);
            _db.SyncJobItems.Add(item);
        }
        await _db.SaveChangesAsync(ct);

        return (true, "Sync job created.", job.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, SyncJobInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error);
        }

        var job = await _db.SyncJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return (false, "Sync job not found.");
        }

        ApplyJob(job, input);

        // Reconcile items by (compose path + environment); update matches, add new, remove the rest.
        var existing = await _db.SyncJobItems.Where(i => i.SyncJobId == id).ToListAsync(ct);
        var keep = new HashSet<long>();
        foreach (var inp in input.Items)
        {
            var path = (inp.ComposePath ?? string.Empty).Trim();
            var match = existing.FirstOrDefault(e => e.ComposePath == path && e.EnvironmentId == inp.EnvironmentId);
            if (match is null)
            {
                var item = new SyncJobItem { SyncJobId = id };
                ApplyItem(item, inp);
                _db.SyncJobItems.Add(item);
            }
            else
            {
                ApplyItem(match, inp);
                keep.Add(match.Id);
            }
        }
        foreach (var e in existing.Where(e => !keep.Contains(e.Id)))
        {
            _db.SyncJobItems.Remove(e); // soft delete; orphaned stack is pruned on the next run if enabled
        }

        await _db.SaveChangesAsync(ct);
        return (true, "Sync job saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var job = await _db.SyncJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return false;
        }

        foreach (var item in await _db.SyncJobItems.Where(i => i.SyncJobId == id).ToListAsync(ct))
        {
            _db.SyncJobItems.Remove(item);
        }
        _db.SyncJobs.Remove(job);
        // Managed stacks are left running; their SyncJobId simply dangles (shown as a normal stack).
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Ok, string Message)> RegenerateTokenAsync(long id, CancellationToken ct = default)
    {
        var job = await _db.SyncJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return (false, "Sync job not found.");
        }

        job.WebhookToken = GenerateToken();
        await _db.SaveChangesAsync(ct);
        return (true, "Webhook URL regenerated.");
    }

    /// <summary>Clones the repo and lists its compose files (for the editor's "Scan" step). When a
    /// <paramref name="subdirectory"/> is given, only compose files under that folder are returned.</summary>
    public async Task<SyncScanResult> ScanAsync(string repoUrl, string? reference, long? credentialId, string? subdirectory = null, CancellationToken ct = default)
    {
        var url = (repoUrl ?? string.Empty).Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncScanResult(false, "Git URL must start with http:// or https://.", null, Array.Empty<string>());
        }

        GitCredentialSecret? secret = null;
        if (credentialId is > 0)
        {
            try { secret = await _gitCredentials.ResolveAsync(credentialId.Value, ct); }
            catch { return new SyncScanResult(false, "Git credentials could not be decrypted.", null, Array.Empty<string>()); }
        }

        GitScanResult scan;
        try
        {
            scan = await _gitRepo.CloneAndScanAsync(url, reference, secret, ct);
        }
        catch (GitOperationException ex)
        {
            return new SyncScanResult(false, ex.Message, null, Array.Empty<string>());
        }

        try
        {
            var files = scan.ComposeFiles;
            var subdir = NormalizeSubdir(subdirectory);
            if (subdir is not null)
            {
                var prefix = subdir + "/";
                files = files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return new SyncScanResult(true, null, scan.CommitSha, files);
        }
        finally
        {
            GitRepositoryService.TryDelete(scan.WorkDir);
        }
    }

    /// <summary>Normalizes a repo subdirectory to a forward-slash path without leading/trailing slashes, or null.</summary>
    private static string? NormalizeSubdir(string? subdirectory)
    {
        if (string.IsNullOrWhiteSpace(subdirectory))
        {
            return null;
        }

        var s = subdirectory.Trim().Replace('\\', '/').Trim('/');
        return s.Length == 0 ? null : s;
    }

    /// <summary>Runs one sync job immediately (manual or webhook) and records the outcome.</summary>
    public async Task<string> RunNowAsync(long id, bool force, CancellationToken ct = default)
    {
        var job = await _db.SyncJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return "Sync job not found.";
        }

        string summary;
        try
        {
            summary = await _runner.RunAsync(job, force, ct);
        }
        catch (Exception ex)
        {
            summary = $"Error: {ex.Message}";
            _logger.LogWarning(ex, "Sync job '{Name}' failed.", job.Name);
        }

        job.LastRunAt = DateTime.UtcNow;
        job.LastStatus = summary;
        job.NextRunAt = job.Enabled ? SafeNext(job.Cron, DateTime.UtcNow) : null;
        await _db.SaveChangesAsync(ct);
        return summary;
    }

    /// <summary>Runs all scheduled jobs whose next run is due; returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var missing = await _db.SyncJobs
            .Where(j => j.Enabled && j.Cron != null && j.Cron != "" && j.NextRunAt == null)
            .ToListAsync(ct);
        if (missing.Count > 0)
        {
            foreach (var j in missing)
            {
                j.NextRunAt = SafeNext(j.Cron, now);
            }
            await _db.SaveChangesAsync(ct);
        }

        var due = await _db.SyncJobs
            .Where(j => j.Enabled && j.NextRunAt != null && j.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var job in due)
        {
            // Advance next-run first so a long/failed run cannot cause a tight re-run loop.
            job.NextRunAt = SafeNext(job.Cron, now);
            await _db.SaveChangesAsync(ct);

            string summary;
            try
            {
                summary = await _runner.RunAsync(job, force: false, ct);
            }
            catch (Exception ex)
            {
                summary = $"Error: {ex.Message}";
                _logger.LogWarning(ex, "Scheduled sync '{Name}' failed.", job.Name);
            }

            job.LastRunAt = DateTime.UtcNow;
            job.LastStatus = summary;
            await _db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    private static void ApplyJob(SyncJob job, SyncJobInput input)
    {
        job.Name = input.Name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        job.GitRepoUrl = input.GitRepoUrl.Trim();
        job.GitReference = string.IsNullOrWhiteSpace(input.GitReference) ? null : input.GitReference.Trim();
        job.Subdirectory = NormalizeSubdir(input.Subdirectory);
        job.GitCredentialId = input.GitCredentialId is > 0 ? input.GitCredentialId : null;
        job.Cron = string.IsNullOrWhiteSpace(input.Cron) ? null : input.Cron.Trim();
        job.Enabled = input.Enabled;
        job.UpdateMode = input.UpdateMode;
        job.PullImages = input.PullImages;
        job.PruneRemoved = input.PruneRemoved;
        job.NextRunAt = job.Enabled && job.Cron is not null ? SafeNext(job.Cron, DateTime.UtcNow) : null;
    }

    private static void ApplyItem(SyncJobItem item, SyncItemInput input)
    {
        item.ComposePath = (input.ComposePath ?? string.Empty).Trim();
        item.EnvironmentId = input.EnvironmentId;
        item.StackName = (input.StackName ?? string.Empty).Trim().ToLowerInvariant();
        item.Enabled = input.Enabled;
    }

    private static string? Validate(SyncJobInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }

        var url = (input.GitRepoUrl ?? string.Empty).Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "Git URL must start with http:// or https://.";
        }

        if (!string.IsNullOrWhiteSpace(input.Cron) && !CronSchedule.IsValid(input.Cron))
        {
            return "Invalid cron expression (5 fields: min hour day month weekday).";
        }

        var seen = new HashSet<(string, long)>();
        foreach (var item in input.Items)
        {
            var path = (item.ComposePath ?? string.Empty).Trim();
            if (path.Length == 0 || VolumeFileCommands.NormalizeRelPath(path) is null)
            {
                return $"Invalid compose path: '{item.ComposePath}'.";
            }
            if (item.EnvironmentId <= 0)
            {
                return $"Choose an environment for '{path}'.";
            }
            var name = (item.StackName ?? string.Empty).Trim().ToLowerInvariant();
            if (!StackCommands.IsValidName(name))
            {
                return $"Invalid stack name '{item.StackName}' (lowercase a-z 0-9 _ -, must start alphanumeric).";
            }
            if (!seen.Add((name, item.EnvironmentId)))
            {
                return $"Duplicate stack name '{name}' for the same environment.";
            }
        }

        return null;
    }

    private static string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static DateTime? SafeNext(string? cron, DateTime fromUtc)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return null;
        }

        try { return CronSchedule.GetNextUtc(cron, fromUtc); }
        catch { return null; }
    }
}
