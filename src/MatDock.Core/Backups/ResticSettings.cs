using System.Text.RegularExpressions;

namespace MatDock.Core.Backups;

/// <summary>What it takes to open a restic repository: the repository, its password and the backend's access keys.</summary>
public sealed record ResticRepoInfo(string Repository, string Password, IReadOnlyDictionary<string, string> Env);

/// <summary>
/// Validation of what an admin types for a restic target. restic is started without a shell (an argument list), so the risk is not
/// quoting but what restic itself will do with a value: <c>RESTIC_PASSWORD_COMMAND</c> runs a program, <c>rclone:</c> starts one, a
/// repository path can point anywhere. So only known backends and the access-key variables of those backends are accepted.
/// </summary>
public static partial class ResticSettings
{
    /// <summary>The tag a snapshot carries with the name of the backup file it holds.</summary>
    public const string FileTagPrefix = "mdfile:";

    /// <summary>The host name MatDock's snapshots carry; listings and restores only look at these, so other snapshots in the repository are left alone.</summary>
    public const string HostName = "matdock";

    private static readonly string[] BackendPrefixes = ["s3:", "b2:", "rest:", "azure:", "gs:", "swift:"];

    // The access keys of the backends. Prefixes, not a list: each backend has a handful (AWS_*, B2_*, AZURE_*, GOOGLE_*, OS_*/ST_* for swift).
    private static readonly string[] EnvPrefixes = ["AWS_", "B2_", "AZURE_", "GOOGLE_", "OS_", "ST_"];
    private static readonly string[] EnvExact = ["RESTIC_REST_USERNAME", "RESTIC_REST_PASSWORD"];

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}\z")]
    private static partial Regex EnvNameRegex();

    // The name of a backup file: what MatDock generates (volume name, time) — no path, no spaces.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,250}\z")]
    private static partial Regex FileNameRegex();

    public static bool IsValidFileName(string? name) => !string.IsNullOrEmpty(name) && FileNameRegex().IsMatch(name);

    [GeneratedRegex(@"(?<=://)[^/@\s]+@")]
    private static partial Regex UserInfoRegex();

    /// <summary>The repository as it may be shown: a <c>user:password@</c> in a URL (a REST server) is dropped.</summary>
    public static string Redact(string? repository) => UserInfoRegex().Replace(repository ?? string.Empty, "***@");

    /// <summary>Null when the repository is acceptable, otherwise what is wrong.</summary>
    public static string? ValidateRepository(string? repository)
    {
        var repo = repository?.Trim();
        if (string.IsNullOrEmpty(repo))
        {
            return "Enter the restic repository.";
        }
        if (repo.Length > 500 || repo.Any(char.IsControl) || repo.StartsWith('-'))
        {
            return "The repository is not valid.";
        }
        if (repo.StartsWith("rclone:", StringComparison.Ordinal) || repo.StartsWith("sftp:", StringComparison.Ordinal))
        {
            return "rclone and sftp repositories are not supported: they need a program or an SSH key inside MatDock. Use S3, B2, a REST server, Azure, GCS or a path.";
        }
        if (repo.StartsWith('/'))
        {
            return repo.Split('/').Contains("..") ? "The path must not contain \"..\"." : null;
        }

        return BackendPrefixes.Any(p => repo.StartsWith(p, StringComparison.Ordinal))
            ? null
            : "Start the repository with s3:, b2:, rest:, azure:, gs:, swift: or an absolute path (/…).";
    }

    /// <summary>Whether <paramref name="name"/> is an access-key variable restic may be given for a backend.</summary>
    public static bool IsAllowedEnvName(string? name)
        => !string.IsNullOrEmpty(name) && EnvNameRegex().IsMatch(name)
           && (EnvExact.Contains(name, StringComparer.Ordinal) || EnvPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>Reads <c>NAME=value</c> lines (blank lines and <c>#</c> comments are skipped). The error names the first line that is not allowed.</summary>
    public static (IReadOnlyDictionary<string, string> Env, string? Error) ParseEnv(string? text)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries))
        {
            lineNo++;
            if (raw.Length == 0 || raw.StartsWith('#'))
            {
                continue;
            }

            var eq = raw.IndexOf('=');
            if (eq <= 0)
            {
                // Never echo the line: it may be a key.
                return (env, $"Line {lineNo} is not a NAME=value line.");
            }

            var name = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            if (!IsAllowedEnvName(name))
            {
                return (env, $"{Shorten(name)} is not an access-key variable restic may be given (allowed: AWS_*, B2_*, AZURE_*, GOOGLE_*, OS_*, ST_*, RESTIC_REST_USERNAME/PASSWORD).");
            }
            if (value.Length > 4000 || value.Any(char.IsControl))
            {
                return (env, $"The value of {name} is not valid.");
            }

            env[name] = value;
        }

        return (env, null);
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
