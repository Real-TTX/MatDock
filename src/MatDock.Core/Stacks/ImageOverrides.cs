using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Images;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MatDock.Core.Stacks;

/// <summary>The image MatDock runs for one service instead of the one in the compose file, and the compose image it was made against.</summary>
/// <param name="Base">The image the compose file named when the override was made (as written, variables and all). When the compose names another one later,
/// the compose was edited on purpose and wins: the override is dropped.</param>
/// <param name="Image">The image MatDock runs.</param>
public sealed record ImageOverride(string Base, string Image);

/// <summary>
/// The compose file (in Git or in the editor) names a starting image; MatDock may run another one per service without touching that file. The choice lives
/// in <see cref="Stack.ImageOverridesJson"/> and reaches compose as a second file, <see cref="FileName"/>, that only sets <c>image:</c>.
/// </summary>
public static class ImageOverrides
{
    /// <summary>The file next to the compose file on the host that compose merges over it.</summary>
    public const string FileName = "matdock-override.yml";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The overrides of a stack by service name (only valid entries).</summary>
    public static IReadOnlyDictionary<string, ImageOverride> Of(Stack stack) => Parse(stack.ImageOverridesJson);

    public static IReadOnlyDictionary<string, ImageOverride> Parse(string? json)
    {
        var result = new SortedDictionary<string, ImageOverride>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            foreach (var (service, entry) in JsonSerializer.Deserialize<Dictionary<string, ImageOverride>>(json, Json) ?? [])
            {
                if (StackCommands.IsValidServiceName(service) && entry is { Base: not null } && ImageCommands.IsValidImageRef(entry.Image))
                {
                    result[service] = entry;
                }
            }
        }
        catch (JsonException)
        {
            // an unreadable value counts as none
        }

        return result;
    }

    public static string? Serialize(IReadOnlyDictionary<string, ImageOverride> overrides)
        => overrides.Count == 0 ? null : JsonSerializer.Serialize(overrides.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => o.Value), Json);

    /// <summary>The <c>image:</c> of each service of a compose file, as written (a service with a <c>build</c> only is not listed).</summary>
    public static IReadOnlyDictionary<string, string> ComposeImages(string? composeYaml)
        => TryComposeImages(composeYaml, out var images) ? images : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The images of a compose file, or false when the text is no YAML or has no <c>services:</c> map (half-typed, or only <c>include:</c>): nothing can be said then.</summary>
    public static bool TryComposeImages(string? composeYaml, out IReadOnlyDictionary<string, string> images)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        images = found;
        if (string.IsNullOrWhiteSpace(composeYaml))
        {
            return false;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(composeYaml));
        }
        catch (YamlException)
        {
            return false;
        }

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode)
            || servicesNode is not YamlMappingNode services)
        {
            return false;
        }

        foreach (var (key, value) in services.Children)
        {
            if (key is YamlScalarNode { Value: { } name }
                && value is YamlMappingNode service
                && service.Children.TryGetValue(new YamlScalarNode("image"), out var image)
                && image is YamlScalarNode { Value: { Length: > 0 } text })
            {
                found[name] = text.Trim();
            }
        }

        return true;
    }

    /// <summary>
    /// What stays of the overrides when the compose file is what it is now: a service that is gone loses its override, and so does one whose compose image is no
    /// longer the one the override was made against (the edit of that line is a decision). <paramref name="notes"/> says what was dropped and why.
    /// </summary>
    public static IReadOnlyDictionary<string, ImageOverride> Reconcile(
        IReadOnlyDictionary<string, ImageOverride> overrides, string? composeYaml, out IReadOnlyList<string> notes)
    {
        var kept = new SortedDictionary<string, ImageOverride>(StringComparer.Ordinal);
        var dropped = new List<string>();
        notes = dropped;
        if (!TryComposeImages(composeYaml, out var images))
        {
            // Half-typed or built from includes: leave the overrides alone rather than lose them to a typo.
            return overrides;
        }

        foreach (var (service, entry) in overrides)
        {
            if (!images.TryGetValue(service, out var current))
            {
                dropped.Add($"{service}: the compose file has no image for this service any more, so its image override ({entry.Image}) is gone.");
            }
            else if (!string.Equals(current, entry.Base, StringComparison.Ordinal))
            {
                dropped.Add($"{service}: the compose file now names {current} (was {entry.Base}), so it wins over the image override ({entry.Image}).");
            }
            else
            {
                kept[service] = entry;
            }
        }

        return kept;
    }

    /// <summary>The text of <see cref="FileName"/>, or null when there is nothing to override.</summary>
    public static string? Render(IReadOnlyDictionary<string, ImageOverride> overrides)
    {
        if (overrides.Count == 0)
        {
            return null;
        }

        // Names and images are validated (no quote, no space, no control character), and quoted all the same: a service called "yes" or "null" stays a string.
        var text = new StringBuilder("# Written by MatDock: the images it runs instead of the ones in the compose file.\nservices:\n");
        foreach (var (service, entry) in overrides.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            text.Append("  \"").Append(service).Append("\":\n    image: \"").Append(entry.Image).Append("\"\n");
        }

        return text.ToString();
    }

    /// <summary>The override file as the base64 line the host commands read, or empty for none.</summary>
    public static string Base64Of(string? overrideYaml)
        => string.IsNullOrEmpty(overrideYaml) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(overrideYaml));
}
