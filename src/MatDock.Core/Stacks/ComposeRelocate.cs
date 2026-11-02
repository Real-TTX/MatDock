using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MatDock.Core.Stacks;

/// <summary>
/// A compose file taken over from a folder on the host (adopt, import) runs from MatDock's own stack folder afterwards. Its relative paths
/// (<c>./data:/data</c>, <c>build: .</c>, <c>env_file: app.env</c>) would then point there, at empty folders instead of the data. This turns every relative
/// host path into an absolute one in the original folder, editing the text in place (comments and formatting stay).
/// </summary>
public static class ComposeRelocate
{
    /// <summary>The file with its relative paths made absolute against <paramref name="folder"/> (a POSIX path on the host), and how many it changed.</summary>
    public static (string Yaml, int Changed) Absolutize(string yaml, string folder)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException)
        {
            return (yaml, 0);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return (yaml, 0);
        }

        var edits = new List<(YamlScalarNode Node, string Value)>();
        if (Child(root, "services") is YamlMappingNode services)
        {
            foreach (var service in services.Children.Values.OfType<YamlMappingNode>())
            {
                Volumes(Child(service, "volumes"), folder, edits);
                Build(Child(service, "build"), folder, edits);
                Files(Child(service, "env_file"), folder, edits);
                if (Child(service, "extends") is YamlMappingNode extends && Child(extends, "file") is YamlScalarNode file && IsRelative(file.Value))
                {
                    edits.Add((file, Join(folder, file.Value!)));
                }
            }
        }

        foreach (var section in new[] { "configs", "secrets" })
        {
            if (Child(root, section) is YamlMappingNode map)
            {
                foreach (var entry in map.Children.Values.OfType<YamlMappingNode>())
                {
                    if (Child(entry, "file") is YamlScalarNode file && IsRelative(file.Value))
                    {
                        edits.Add((file, Join(folder, file.Value!)));
                    }
                }
            }
        }

        return (Apply(yaml, edits), edits.Count);
    }

    private static void Volumes(YamlNode? node, string folder, List<(YamlScalarNode, string)> edits)
    {
        if (node is not YamlSequenceNode list)
        {
            return;
        }

        foreach (var item in list.Children)
        {
            if (item is YamlScalarNode scalar && scalar.Value is { } value)
            {
                var colon = value.IndexOf(':');
                if (colon > 0 && IsRelative(value[..colon]))
                {
                    edits.Add((scalar, Join(folder, value[..colon]) + value[colon..]));
                }
            }
            else if (item is YamlMappingNode map && Child(map, "source") is YamlScalarNode source
                     && (Child(map, "type") as YamlScalarNode)?.Value is null or "bind" && IsRelative(source.Value))
            {
                edits.Add((source, Join(folder, source.Value!)));
            }
        }
    }

    private static void Build(YamlNode? node, string folder, List<(YamlScalarNode, string)> edits)
    {
        if (node is YamlScalarNode context && IsBuildContext(context.Value))
        {
            edits.Add((context, Join(folder, context.Value!)));
        }
        else if (node is YamlMappingNode map && Child(map, "context") is YamlScalarNode ctx && IsBuildContext(ctx.Value))
        {
            edits.Add((ctx, Join(folder, ctx.Value!)));
        }
    }

    private static void Files(YamlNode? node, string folder, List<(YamlScalarNode, string)> edits)
    {
        IEnumerable<YamlNode> items = node switch
        {
            YamlScalarNode scalar => [scalar],
            YamlSequenceNode list => list.Children,
            _ => [],
        };

        foreach (var item in items)
        {
            var scalar = item as YamlScalarNode ?? (item is YamlMappingNode map ? Child(map, "path") as YamlScalarNode : null);
            if (scalar?.Value is { } value && !value.StartsWith('/') && !value.Contains("${", StringComparison.Ordinal))
            {
                edits.Add((scalar, Join(folder, value)));
            }
        }
    }

    /// <summary>A host path compose reads relative to the project folder (<c>.</c>, <c>./x</c>, <c>../x</c>); a name without a dot is a volume.</summary>
    private static bool IsRelative(string? path)
        => path is { Length: > 0 } && (path == "." || path == ".." || path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal));

    /// <summary>A build context that is a folder of the project (not a URL, not absolute, no variable).</summary>
    private static bool IsBuildContext(string? context)
        => context is { Length: > 0 } && !context.StartsWith('/') && !context.Contains("://", StringComparison.Ordinal)
           && !context.StartsWith("git@", StringComparison.Ordinal) && !context.Contains("${", StringComparison.Ordinal);

    /// <summary>The folder and a relative path joined and normalised (<c>.</c> and <c>..</c> resolved, POSIX).</summary>
    public static string Join(string folder, string relative)
    {
        var parts = new List<string>(folder.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    /// <summary>Replaces each scalar's text on its line (the scalars of a compose file are one line each; others are left alone), from the end backwards.</summary>
    private static string Apply(string yaml, List<(YamlScalarNode Node, string Value)> edits)
    {
        if (edits.Count == 0)
        {
            return yaml;
        }

        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        foreach (var (node, value) in edits.OrderByDescending(e => e.Node.Start.Line).ThenByDescending(e => e.Node.Start.Column))
        {
            if (node.Start.Line != node.End.Line || node.Start.Line < 1 || node.Start.Line > lines.Length)
            {
                continue;
            }

            var line = lines[node.Start.Line - 1];
            var from = (int)node.Start.Column - 1;
            var to = (int)node.End.Column - 1;
            if (from < 0 || to > line.Length || from >= to)
            {
                continue;
            }

            lines[node.Start.Line - 1] = line[..from] + Quote(value, node.Style) + line[to..];
        }

        return string.Join(newline, lines);
    }

    private static string Quote(string value, ScalarStyle style) => style switch
    {
        ScalarStyle.SingleQuoted => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
        ScalarStyle.DoubleQuoted => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        _ => value.IndexOfAny([' ', '#', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0 ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value,
    };

    private static YamlNode? Child(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
