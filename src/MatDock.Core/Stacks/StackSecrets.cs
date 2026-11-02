using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatDock.Core.Stacks;

/// <summary>
/// The secret variables of a stack (<see cref="Entities.Stack.SecretsJson"/>): names and values, kept encrypted in the database, never written to the host.
/// A deploy hands them to compose as environment variables (<see cref="Exports"/>), so <c>${NAME}</c> in the compose file resolves to them; a container
/// sees one only where the file names it (<c>environment: DB_PASSWORD: ${DB_PASSWORD}</c>). The pages show their names, never their values.
/// </summary>
public static partial class StackSecrets
{
    /// <summary>The longest value (a certificate fits).</summary>
    public const int MaxValue = 32_000;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string? name) => !string.IsNullOrEmpty(name) && NameRegex().IsMatch(name);

    /// <summary>Why a value cannot be a secret (a line break, too long), or null.</summary>
    public static string? Problem(string name, string value)
    {
        if (!IsValidName(name))
        {
            return $"'{name}' is not a variable name (letters, digits and _, not starting with a digit).";
        }

        if (value.Contains('\n') || value.Contains('\r'))
        {
            return $"The value of {name} has a line break; a secret is one line.";
        }

        return value.Length > MaxValue ? $"The value of {name} is too long." : null;
    }

    public static SortedDictionary<string, string> Parse(string? json)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
            {
                if (IsValidName(key))
                {
                    result[key] = value ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // an unreadable value counts as none
        }

        return result;
    }

    /// <summary>JSON for the stack, null when there is none.</summary>
    public static string? ToJson(IReadOnlyDictionary<string, string> secrets) => secrets.Count == 0 ? null : JsonSerializer.Serialize(secrets);

    /// <summary>The names, sorted.</summary>
    public static IReadOnlyList<string> Names(string? json) => Parse(json).Keys.ToList();

    /// <summary>
    /// What a deploy hands to compose as environment variables: the variables of <paramref name="envContent"/> (only for a Git stack, where they override the
    /// repository's <c>.env</c>), then the secrets, one <c>NAME=value</c> per line. Null when there is nothing.
    /// </summary>
    public static string? Exports(string? secretsJson, string? envContent = null)
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in StackVariables.ParseEnv(envContent))
        {
            all[key] = value;
        }

        foreach (var (key, value) in Parse(secretsJson))
        {
            all[key] = value;
        }

        if (all.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var (key, value) in all)
        {
            if (Problem(key, value) is null)
            {
                text.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }
}

/// <summary>
/// The variables a compose file uses (<c>${NAME}</c>, <c>$NAME</c>, with <c>:-default</c>, <c>-default</c>, <c>:?error</c> …) and which of them have no
/// value: not in the <c>.env</c>, not a secret, not one compose sets itself.
/// </summary>
public static partial class StackVariables
{
    /// <summary>What compose and the shell set themselves (they never count as missing).</summary>
    public static readonly IReadOnlySet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal)
    {
        "COMPOSE_PROJECT_NAME", "COMPOSE_FILE", "COMPOSE_PROFILES", "COMPOSE_PATH_SEPARATOR", "COMPOSE_CONVERT_WINDOWS_PATHS", "COMPOSE_PARALLEL_LIMIT",
        "COMPOSE_IGNORE_ORPHANS", "COMPOSE_REMOVE_ORPHANS", "COMPOSE_ENV_FILES", "COMPOSE_MENU", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_DEFAULT_PLATFORM",
        "DOCKER_BUILDKIT", "PWD", "HOME", "USER", "PATH", "HOSTNAME",
    };

    /// <summary>One use of a variable in the file.</summary>
    /// <param name="Line">1-based.</param>
    /// <param name="Required">Written <c>${NAME:?message}</c> or <c>${NAME?message}</c>: compose refuses the file without it.</param>
    /// <param name="HasDefault">Written with <c>-</c>, <c>:-</c>, <c>+</c> or <c>:+</c>: without a value compose takes the default.</param>
    public sealed record Use(string Name, int Line, bool Required, bool HasDefault);

    // $$ is an escaped dollar; ${NAME...} or $NAME.
    [GeneratedRegex(@"\$\$|\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<op>:?[-?+])?[^}]*\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ReferenceRegex();

    /// <summary>The uses of variables in a compose file (comments skipped).</summary>
    public static IReadOnlyList<Use> Uses(string? composeYaml)
    {
        var uses = new List<Use>();
        var lines = (composeYaml ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (Match m in ReferenceRegex().Matches(line))
            {
                if (m.Value == "$$")
                {
                    continue;
                }

                var op = m.Groups["op"].Value;
                var name = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["bare"].Value;
                uses.Add(new Use(name, i + 1, op.EndsWith('?'), op.EndsWith('-') || op.EndsWith('+')));
            }
        }

        return uses;
    }

    /// <summary>The variables of a <c>.env</c> text (<c>NAME=value</c>, <c>export NAME=value</c>, quotes removed; comments and blank lines skipped).</summary>
    public static IReadOnlyDictionary<string, string> ParseEnv(string? envContent)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (envContent ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }

            if (StackSecrets.IsValidName(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// The variables the file uses without a value: not in <paramref name="defined"/> (the <c>.env</c>, the secrets), not built in, and not written with a
    /// default. Each once, at its first line; <c>Required</c> when one of its uses is <c>:?</c>.
    /// </summary>
    public static IReadOnlyList<Use> Missing(string? composeYaml, IEnumerable<string> defined)
    {
        var known = new HashSet<string>(defined, StringComparer.Ordinal);
        return Uses(composeYaml)
            .Where(u => !known.Contains(u.Name) && !BuiltIn.Contains(u.Name))
            .GroupBy(u => u.Name)
            .Where(g => g.Any(u => !u.HasDefault))
            .Select(g => g.First() with { Required = g.Any(u => u.Required) })
            .OrderBy(u => u.Line)
            .ToList();
    }
}
