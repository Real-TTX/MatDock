using System.Globalization;
using MatDock.Core.Containers;

namespace MatDock.Core.Networks;

/// <summary>A box of the network map: a network (left) or a container (right).</summary>
public sealed record MapNode(string Key, string Label, string? Sub, int X, int Y, int Width, int Height, string? State, int Links);

/// <summary>A line from a network to a container, as an SVG path.</summary>
public sealed record MapEdge(string Network, string ContainerId, string Path);

/// <summary>
/// Which containers share which networks, laid out as two columns: the networks with containers on the left, the
/// containers on the right, a line for every attachment. The containers are ordered by the networks they hang on (the
/// mean position of those), so the lines cross as little as a simple order allows; a container on several networks is
/// how services of different networks reach each other.
/// </summary>
public sealed record NetworkMap(int Width, int Height, IReadOnlyList<MapNode> Networks, IReadOnlyList<MapNode> Containers, IReadOnlyList<MapEdge> Edges)
{
    public const int Width0 = 960;
    private const int BoxWidth = 300;
    private const int BoxHeight = 40;
    private const int Gap = 12;
    private const int Pad = 16;

    public static NetworkMap Build(IReadOnlyList<DockerContainer> containers)
    {
        var attached = containers.Where(c => c.Networks.Count > 0).ToList();
        var networks = attached.SelectMany(c => c.Networks)
            .GroupBy(n => n, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var index = networks.Select((n, i) => (n.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);

        var ordered = attached
            .OrderBy(c => c.Networks.Average(n => index[n]))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var leftHeight = Column(networks.Count);
        var rightHeight = Column(ordered.Count);
        var height = Math.Max(leftHeight, rightHeight) + 2 * Pad;
        var leftTop = Pad + (Math.Max(leftHeight, rightHeight) - leftHeight) / 2;
        var rightTop = Pad + (Math.Max(leftHeight, rightHeight) - rightHeight) / 2;

        var netNodes = networks.Select((n, i) => new MapNode(n.Name, n.Name, n.Count == 1 ? "1 container" : $"{n.Count} containers",
            Pad, leftTop + i * (BoxHeight + Gap), BoxWidth, BoxHeight, null, n.Count)).ToList();
        var containerX = Width0 - Pad - BoxWidth;
        var conNodes = ordered.Select((c, i) => new MapNode(c.Id, c.Name, c.Project is null ? c.Image : $"{c.Project} · {c.Service ?? c.Image}",
            containerX, rightTop + i * (BoxHeight + Gap), BoxWidth, BoxHeight, c.IsRunning ? "running" : "stopped", c.Networks.Count)).ToList();

        var edges = new List<MapEdge>();
        foreach (var (c, node) in ordered.Zip(conNodes))
        {
            foreach (var n in c.Networks)
            {
                var from = netNodes[index[n]];
                double x1 = from.X + from.Width, y1 = from.Y + BoxHeight / 2.0, x2 = node.X, y2 = node.Y + BoxHeight / 2.0;
                var mid = (x1 + x2) / 2;
                edges.Add(new MapEdge(n, c.Id, string.Create(CultureInfo.InvariantCulture, $"M{x1},{y1} C{mid},{y1} {mid},{y2} {x2},{y2}")));
            }
        }

        return new NetworkMap(Width0, height, netNodes, conNodes, edges);
    }

    private static int Column(int count) => count == 0 ? 0 : count * BoxHeight + (count - 1) * Gap;
}
