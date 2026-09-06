using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MatDock.Core.Apps;

/// <summary>
/// Reads <see cref="AppMetadata"/> from the compose <c>x-matdock:</c> extension block and/or a sidecar
/// file, and (de)serializes the resolved metadata to JSON for storage. All parsing is best-effort — a
/// malformed block never throws to the caller (returns null), so it can't break a deploy or a page.
/// </summary>
public static class AppMetadataParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer RawYaml = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Extracts the <c>x-matdock:</c> block from a compose YAML document.</summary>
    public static AppMetadata? FromCompose(string? composeYaml)
    {
        if (string.IsNullOrWhiteSpace(composeYaml))
        {
            return null;
        }

        try
        {
            // Read the whole document as an untyped map (dictionary keys are literal — the "x-matdock"
            // extension field survives), then re-serialize that node and map it to AppMetadata. Avoids
            // alias/naming-convention pitfalls on the compose root.
            var root = RawYaml.Deserialize<Dictionary<string, object?>>(composeYaml);
            if (root is null || !root.TryGetValue("x-matdock", out var node) || node is null)
            {
                return null;
            }

            return Clean(Yaml.Deserialize<AppMetadata>(YamlSerializer.Serialize(node)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses a sidecar file (JSON when the name ends in .json, otherwise YAML).</summary>
    public static AppMetadata? FromSidecar(string? content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var meta = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize<AppMetadata>(content, JsonOpts)
                : Yaml.Deserialize<AppMetadata>(content);
            return Clean(meta);
        }
        catch
        {
            return null;
        }
    }

    public static string ToJson(AppMetadata meta) => JsonSerializer.Serialize(meta);

    public static AppMetadata? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppMetadata>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Overlays the non-empty fields of <paramref name="overlay"/> onto <paramref name="baseMeta"/>.</summary>
    public static AppMetadata? Merge(AppMetadata? baseMeta, AppMetadata? overlay)
    {
        if (baseMeta is null)
        {
            return Clean(overlay);
        }
        if (overlay is null)
        {
            return Clean(baseMeta);
        }

        return Clean(new AppMetadata
        {
            Name = Pick(overlay.Name, baseMeta.Name),
            Icon = Pick(overlay.Icon, baseMeta.Icon),
            Description = Pick(overlay.Description, baseMeta.Description),
            Category = Pick(overlay.Category, baseMeta.Category),
            Actions = overlay.Actions.Count > 0 ? overlay.Actions : baseMeta.Actions,
        });
    }

    private static string? Pick(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static AppMetadata? Clean(AppMetadata? m)
    {
        if (m is null)
        {
            return null;
        }

        m.Actions = (m.Actions ?? new List<AppAction>())
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Url))
            .Select(a => new AppAction { Name = a.Name.Trim(), Url = a.Url.Trim(), Icon = a.Icon?.Trim(), Port = a.Port })
            .ToList();

        return m.IsEmpty ? null : m;
    }
}
