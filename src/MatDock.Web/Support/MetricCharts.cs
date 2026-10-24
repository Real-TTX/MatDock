using System.Globalization;
using MatDock.Core.Docker;

namespace MatDock.Web.Support;

/// <summary>The charts of the readings of a host or a container over a range (<c>_MetricCharts</c>); <see cref="Link"/> is the page with <c>range=</c>.</summary>
public sealed record MetricCharts(IReadOnlyList<MetricPoint> Points, string Range, Func<string, string> Link, bool Host)
{
    public sealed record Series(string Label, string Css, IReadOnlyList<double> Values, string Now, string Avg, string Max);

    public IReadOnlyList<Series> All()
    {
        var list = new List<Series> { Of("CPU", "md-spark__cpu", Points.Select(p => p.Cpu).ToList()), Of("Memory", "md-spark__mem", Points.Select(p => p.Mem).ToList()) };
        if (Host && Points.Any(p => p.Disk is not null))
        {
            list.Add(Of("Disk", "md-spark__disk", Points.Where(p => p.Disk is not null).Select(p => p.Disk!.Value).ToList()));
        }

        return list;
    }

    private static Series Of(string label, string css, IReadOnlyList<double> values)
    {
        string P(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " %";
        return values.Count == 0
            ? new Series(label, css, values, "–", "–", "–")
            : new Series(label, css, values, P(values[^1]), P(values.Average()), P(values.Max()));
    }
}
