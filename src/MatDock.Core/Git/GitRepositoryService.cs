using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Git;

/// <summary>Result of cloning a repo and scanning it for compose files.</summary>
public sealed record GitScanResult(string WorkDir, string? CommitSha, IReadOnlyList<string> ComposeFiles);

/// <summary>A clone/checkout failed; <see cref="Message"/> is safe to show the user (no secrets).</summary>
public sealed class GitOperationException : Exception
{
    public GitOperationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Clones stack repositories into a temporary working directory using LibGit2Sharp (self-contained, so
/// no <c>git</c> binary is required in the container or on the Windows EXE host). The <c>.git</c> folder
/// is removed after checkout so only the working tree is shipped to the Docker host.
/// </summary>
public sealed partial class GitRepositoryService
{
    private readonly ILogger<GitRepositoryService> _logger;

    public GitRepositoryService(ILogger<GitRepositoryService> logger)
    {
        _logger = logger;
    }

    /// <summary>Clones <paramref name="repoUrl"/> (optionally a branch/tag) into a fresh temp dir and
    /// returns that path. Throws <see cref="GitOperationException"/> on failure (temp dir cleaned up).</summary>
    public Task<string> CloneToTempAsync(string repoUrl, string? reference, GitCredentialSecret? secret, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var workDir = Path.Combine(Path.GetTempPath(), "matdock-git-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new CloneOptions();
                if (!string.IsNullOrWhiteSpace(reference))
                {
                    options.BranchName = reference.Trim();
                }

                if (secret is not null && !string.IsNullOrEmpty(secret.Token))
                {
                    options.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
                    {
                        Username = string.IsNullOrEmpty(secret.Username) ? "git" : secret.Username,
                        Password = secret.Token,
                    };
                }

                ct.ThrowIfCancellationRequested();
                Repository.Clone(repoUrl, workDir, options);

                // The .git history is not needed on the target host and would bloat the transfer.
                // Best-effort only: a transient lock on a pack file (AV/indexer on Windows) must NOT
                // turn a successful clone into a failed deploy — a leftover .git just ships along.
                var gitDir = Path.Combine(workDir, ".git");
                if (Directory.Exists(gitDir))
                {
                    try { DeleteDirectoryRobust(gitDir); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Could not remove .git from clone; shipping it along."); }
                }

                return workDir;
            }
            catch (OperationCanceledException)
            {
                TryDelete(workDir);
                throw; // surface as cancellation, not a git error
            }
            catch (Exception ex) when (ex is not GitOperationException)
            {
                TryDelete(workDir);
                _logger.LogInformation(ex, "Git clone of {Repo} failed.", Sanitize(repoUrl));
                throw new GitOperationException(Describe(ex, !string.IsNullOrEmpty(secret?.Token)), ex);
            }
        }, ct);

    // A file is treated as a compose file if its name is compose.yml/.yaml or docker-compose*.yml/.yaml
    // (also .prod./.override. variants). Case-insensitive.
    [GeneratedRegex(@"^(docker-)?compose([.][\w.-]+)?\.ya?ml$", RegexOptions.IgnoreCase)]
    private static partial Regex ComposeFileRegex();

    /// <summary>
    /// Clones the repo, records the checked-out commit and enumerates every compose file in the tree.
    /// The caller owns <see cref="GitScanResult.WorkDir"/> and must <see cref="TryDelete"/> it when done.
    /// Throws <see cref="GitOperationException"/> on failure (temp dir cleaned up).
    /// </summary>
    public Task<GitScanResult> CloneAndScanAsync(string repoUrl, string? reference, GitCredentialSecret? secret, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var workDir = Path.Combine(Path.GetTempPath(), "matdock-git-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new CloneOptions();
                if (!string.IsNullOrWhiteSpace(reference))
                {
                    options.BranchName = reference.Trim();
                }

                if (secret is not null && !string.IsNullOrEmpty(secret.Token))
                {
                    options.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
                    {
                        Username = string.IsNullOrEmpty(secret.Username) ? "git" : secret.Username,
                        Password = secret.Token,
                    };
                }

                ct.ThrowIfCancellationRequested();
                Repository.Clone(repoUrl, workDir, options);

                // Capture the commit BEFORE .git is removed (used for change detection).
                string? sha = null;
                try
                {
                    using var repo = new Repository(workDir);
                    sha = repo.Head.Tip?.Sha;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read HEAD sha from clone.");
                }

                var composeFiles = ScanComposeFiles(workDir);

                var gitDir = Path.Combine(workDir, ".git");
                if (Directory.Exists(gitDir))
                {
                    try { DeleteDirectoryRobust(gitDir); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Could not remove .git from clone; shipping it along."); }
                }

                return new GitScanResult(workDir, sha, composeFiles);
            }
            catch (OperationCanceledException)
            {
                TryDelete(workDir);
                throw;
            }
            catch (Exception ex) when (ex is not GitOperationException)
            {
                TryDelete(workDir);
                _logger.LogInformation(ex, "Git clone/scan of {Repo} failed.", Sanitize(repoUrl));
                throw new GitOperationException(Describe(ex, !string.IsNullOrEmpty(secret?.Token)), ex);
            }
        }, ct);

    /// <summary>
    /// Resolves the remote commit SHA of the given branch/tag (or the default HEAD) WITHOUT cloning
    /// (git ls-remote equivalent). Returns null on any error — callers fall back to cloning.
    /// </summary>
    public Task<string?> GetRemoteHeadShaAsync(string repoUrl, string? reference, GitCredentialSecret? secret, CancellationToken ct = default)
        => Task.Run<string?>(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                List<Reference> refs;
                if (secret is not null && !string.IsNullOrEmpty(secret.Token))
                {
                    refs = Repository.ListRemoteReferences(repoUrl, (_, _, _) => new UsernamePasswordCredentials
                    {
                        Username = string.IsNullOrEmpty(secret.Username) ? "git" : secret.Username,
                        Password = secret.Token,
                    }).ToList();
                }
                else
                {
                    refs = Repository.ListRemoteReferences(repoUrl).ToList();
                }

                string? Find(string canonical) =>
                    refs.FirstOrDefault(r => string.Equals(r.CanonicalName, canonical, StringComparison.Ordinal))?.TargetIdentifier;

                var refName = string.IsNullOrWhiteSpace(reference) ? null : reference!.Trim();
                if (refName is not null)
                {
                    return Find($"refs/heads/{refName}") ?? Find($"refs/tags/{refName}")
                        ?? refs.FirstOrDefault(r => r.CanonicalName.EndsWith("/" + refName, StringComparison.Ordinal))?.TargetIdentifier;
                }

                // No explicit ref: follow the remote HEAD symref to its branch, then to the sha.
                var target = refs.FirstOrDefault(r => r.CanonicalName == "HEAD")?.TargetIdentifier;
                if (target is not null && target.StartsWith("refs/", StringComparison.Ordinal))
                {
                    return Find(target);
                }

                return target;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Remote head lookup for {Repo} failed.", Sanitize(repoUrl));
                return null;
            }
        }, ct);

    /// <summary>Enumerates repo-relative (POSIX) paths of every compose file in the working tree.</summary>
    public static IReadOnlyList<string> ScanComposeFiles(string workDir)
    {
        var results = new List<string>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return results;
        }

        foreach (var file in files)
        {
            if (!ComposeFileRegex().IsMatch(Path.GetFileName(file)))
            {
                continue;
            }

            var rel = Path.GetRelativePath(workDir, file).Replace(Path.DirectorySeparatorChar, '/');
            // Skip VCS/dependency noise that legitimately ships compose files.
            if (rel.Split('/').Any(seg => seg is ".git" or "node_modules"))
            {
                continue;
            }

            results.Add(rel);
            if (results.Count >= 500)
            {
                break;
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>Best-effort recursive delete of a working directory (safe to call on a temp clone).</summary>
    public static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                DeleteDirectoryRobust(dir);
            }
        }
        catch
        {
            // best effort – temp dirs are reclaimed by the OS eventually.
        }
    }

    /// <summary>Strips any <c>user:pass@</c> userinfo from a URL so a credential typed into the URL
    /// field is not written to the logs.</summary>
    private static string Sanitize(string url)
    {
        try
        {
            var u = new Uri(url);
            return string.IsNullOrEmpty(u.UserInfo) ? url : url.Replace(u.UserInfo + "@", string.Empty);
        }
        catch
        {
            return url;
        }
    }

    private static string Describe(Exception ex, bool hadToken)
    {
        var msg = ex.Message;
        if (ex is LibGit2SharpException && (msg.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                                            || msg.Contains("401") || msg.Contains("403")
                                            || msg.Contains("credentials", StringComparison.OrdinalIgnoreCase)))
        {
            return hadToken
                ? "Authentication failed - check the user/token."
                : "Authentication required - assign Git credentials.";
        }

        if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) || msg.Contains("404"))
        {
            return "Repository not found - check the URL.";
        }

        if (msg.Contains("Cannot checkout", StringComparison.OrdinalIgnoreCase) || msg.Contains("no reference", StringComparison.OrdinalIgnoreCase))
        {
            return "Branch/tag not found.";
        }

        // The generic message can echo the raw repo URL, which may carry user:token@host credentials
        // (this string is stored in LastStatus and logged on the webhook path) — strip any userinfo.
        return "Git error: " + StripUserInfo(msg);
    }

    /// <summary>Removes <c>user:pass@</c> userinfo from any http(s) URL inside a free-text message.</summary>
    private static string StripUserInfo(string text)
        => UserInfoRegex().Replace(text, "$1");

    [GeneratedRegex(@"(https?://)[^/\s@]+@")]
    private static partial Regex UserInfoRegex();

    /// <summary>Deletes a directory tree, clearing the read-only attribute that Git sets on pack files
    /// (otherwise <see cref="Directory.Delete(string, bool)"/> throws on Windows).</summary>
    private static void DeleteDirectoryRobust(string dir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(entry, FileAttributes.Normal); } catch { /* ignore */ }
        }

        Directory.Delete(dir, recursive: true);
    }
}
