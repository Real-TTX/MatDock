using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Git;

/// <summary>CRUD for saved Git repositories plus a connectivity test (clone + compose scan).</summary>
public sealed class GitRepoService
{
    private readonly MatDockDbContext _db;
    private readonly GitCredentialService _credentials;
    private readonly GitRepositoryService _gitRepo;

    public GitRepoService(MatDockDbContext db, GitCredentialService credentials, GitRepositoryService gitRepo)
    {
        _db = db;
        _credentials = credentials;
        _gitRepo = gitRepo;
    }

    public Task<List<GitRepo>> GetAllAsync(CancellationToken ct = default)
        => _db.GitRepos.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);

    public Task<GitRepo?> GetAsync(long id, CancellationToken ct = default)
        => _db.GitRepos.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(GitRepoInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error, 0);
        }

        var repo = new GitRepo();
        Apply(repo, input);
        _db.GitRepos.Add(repo);
        await _db.SaveChangesAsync(ct);
        return (true, "Git repository saved.", repo.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, GitRepoInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error);
        }

        var repo = await _db.GitRepos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null)
        {
            return (false, "Git repository not found.");
        }

        Apply(repo, input);
        await _db.SaveChangesAsync(ct);
        return (true, "Git repository saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var repo = await _db.GitRepos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null)
        {
            return false;
        }

        _db.GitRepos.Remove(repo);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Clones the repo and reports reachability + how many compose files were found.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string url, string? reference, long? credentialId, CancellationToken ct = default)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Git URL must start with http:// or https://.");
        }

        GitCredentialSecret? secret = null;
        if (credentialId is > 0)
        {
            try { secret = await _credentials.ResolveAsync(credentialId.Value, ct); }
            catch { return (false, "Git credentials could not be decrypted."); }
            if (secret is null)
            {
                return (false, "The assigned Git credentials were not found.");
            }
        }

        GitScanResult scan;
        try
        {
            scan = await _gitRepo.CloneAndScanAsync(trimmed, reference, secret, ct);
        }
        catch (GitOperationException ex)
        {
            return (false, ex.Message);
        }

        try
        {
            var sha = scan.CommitSha is { Length: > 7 } s ? s[..7] : scan.CommitSha;
            return (true, $"Reachable — {scan.ComposeFiles.Count} compose file(s) found" + (sha is null ? "." : $" (HEAD {sha})."));
        }
        finally
        {
            GitRepositoryService.TryDelete(scan.WorkDir);
        }
    }

    private static void Apply(GitRepo repo, GitRepoInput input)
    {
        repo.Name = input.Name.Trim();
        repo.Url = input.Url.Trim();
        repo.Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim();
        repo.GitCredentialId = input.GitCredentialId is > 0 ? input.GitCredentialId : null;
    }

    private static string? Validate(GitRepoInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }

        var url = (input.Url ?? string.Empty).Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "Git URL must start with http:// or https://.";
        }

        return null;
    }
}
