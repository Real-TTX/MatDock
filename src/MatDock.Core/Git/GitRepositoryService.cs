using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Git;

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
public sealed class GitRepositoryService
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
                var gitDir = Path.Combine(workDir, ".git");
                if (Directory.Exists(gitDir))
                {
                    DeleteDirectoryRobust(gitDir);
                }

                return workDir;
            }
            catch (Exception ex) when (ex is not GitOperationException)
            {
                TryDelete(workDir);
                _logger.LogInformation(ex, "Git clone of {Repo} failed.", repoUrl);
                throw new GitOperationException(Describe(ex, !string.IsNullOrEmpty(secret?.Token)), ex);
            }
        }, ct);

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

    private static string Describe(Exception ex, bool hadToken)
    {
        var msg = ex.Message;
        if (ex is LibGit2SharpException && (msg.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                                            || msg.Contains("401") || msg.Contains("403")
                                            || msg.Contains("credentials", StringComparison.OrdinalIgnoreCase)))
        {
            return hadToken
                ? "Authentifizierung fehlgeschlagen – Benutzer/Token prüfen."
                : "Authentifizierung erforderlich – Git-Zugang zuweisen.";
        }

        if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) || msg.Contains("404"))
        {
            return "Repository nicht gefunden – URL prüfen.";
        }

        if (msg.Contains("Cannot checkout", StringComparison.OrdinalIgnoreCase) || msg.Contains("no reference", StringComparison.OrdinalIgnoreCase))
        {
            return "Branch/Tag nicht gefunden.";
        }

        return "Git-Fehler: " + msg;
    }

    /// <summary>Deletes a directory tree, clearing the read-only attribute that Git sets on pack files
    /// (otherwise <see cref="Directory.Delete(string, bool)"/> throws on Windows).</summary>
    private static void DeleteDirectoryRobust(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* ignore */ }
        }

        Directory.Delete(dir, recursive: true);
    }
}
