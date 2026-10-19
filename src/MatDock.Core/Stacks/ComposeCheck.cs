using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MatDock.Core.Stacks;

/// <summary>
/// A quick check of a compose file before it is saved from the editor: valid YAML, a <c>services</c> map, and every
/// service with an <c>image</c> or a <c>build</c>. It finds the typing mistakes early (with the line), it does not
/// replace <c>docker compose config</c> on the host (variables, extends, profiles stay its job).
/// </summary>
public static class ComposeCheck
{
    /// <summary>The first problem, or null when the file looks like a compose file.</summary>
    public static string? Problem(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return "The compose file is empty.";
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            var colon = message.IndexOf("): ", StringComparison.Ordinal);
            return $"YAML error in line {ex.Start.Line}: {(colon >= 0 ? message[(colon + 3)..] : message)}";
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return "The compose file must be a map with 'services:'.";
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode) || servicesNode is not YamlMappingNode services)
        {
            // A file that only includes others is valid compose.
            return root.Children.ContainsKey(new YamlScalarNode("include")) ? null : "The compose file has no 'services:' map.";
        }

        foreach (var (key, value) in services.Children)
        {
            var name = (key as YamlScalarNode)?.Value ?? "?";
            if (value is not YamlMappingNode service)
            {
                return $"Service '{name}' (line {key.Start.Line}) is not a map.";
            }

            var hasImage = service.Children.ContainsKey(new YamlScalarNode("image"));
            var hasBuild = service.Children.ContainsKey(new YamlScalarNode("build"));
            var extends = service.Children.ContainsKey(new YamlScalarNode("extends"));
            if (!hasImage && !hasBuild && !extends)
            {
                return $"Service '{name}' (line {key.Start.Line}) needs an 'image' or a 'build'.";
            }
        }

        return null;
    }
}
