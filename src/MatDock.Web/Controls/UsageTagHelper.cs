using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// RAM and CPU of a container or a stack as two MatUi meters with their value (<c>mat-meter</c>, the width comes from
/// <c>data-value</c>, no inline style). <c>large</c> is the variant of a tile: taller meters, the RAM as text ("120MiB / 2GiB")
/// and always shown; the list variant shows a dash when nothing is known.
/// <code>&lt;usage mem="@c.MemPercent" cpu="@c.CpuPercent" mem-text="@c.MemUsage" /&gt;</code>
/// </summary>
[HtmlTargetElement("usage", TagStructure = TagStructure.WithoutEndTag)]
public sealed class UsageTagHelper : TagHelper
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>RAM in percent of the host.</summary>
    public double? Mem { get; set; }

    /// <summary>CPU in percent of the host.</summary>
    public double? Cpu { get; set; }

    /// <summary>The RAM as Docker reports it ("120MiB / 2GiB").</summary>
    public string? MemText { get; set; }

    /// <summary>Tooltip of the RAM row (default: the RAM text and the percentage).</summary>
    public string? MemTitle { get; set; }

    /// <summary>Tooltip of the CPU row (default: the percentage).</summary>
    public string? CpuTitle { get; set; }

    public bool Large { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagMode = TagMode.StartTagAndEndTag;
        if (!Large && Cpu is null && Mem is null && string.IsNullOrEmpty(MemText))
        {
            output.TagName = "span";
            output.Attributes.SetAttribute("class", "mat-muted");
            output.Content.SetContent("–");
            return;
        }

        output.TagName = "div";
        output.Attributes.SetAttribute("class", Large ? "md-usage md-usage--lg" : "md-usage");

        var memTitle = MemTitle ?? $"RAM: {(string.IsNullOrEmpty(MemText) ? "n/a" : MemText)}{(Mem is { } mp ? $" ({mp.ToString("0.0", Ci)}%)" : "")}";
        var cpuTitle = CpuTitle ?? $"CPU: {(Cpu is { } cp ? cp.ToString("0.0", Ci) + "%" : "n/a")}";
        var memValue = Large
            ? (string.IsNullOrEmpty(MemText) ? "–" : MemText)
            : (Mem is { } m ? Math.Round(m).ToString(Ci) + "%" : "–");
        var cpuValue = Cpu is { } c ? c.ToString("0.0", Ci) + "%" : "–";

        var sb = new StringBuilder();
        Row(sb, "RAM", Mem, memValue, memTitle);
        Row(sb, "CPU", Cpu, cpuValue, cpuTitle);
        output.Content.SetHtmlContent(sb.ToString());
    }

    private void Row(StringBuilder sb, string label, double? percent, string value, string title)
    {
        var pct = Math.Clamp(percent ?? 0, 0, 100).ToString("0.#", Ci);
        sb.Append("<div class=\"md-usage__row\" title=\"").Append(WebUtility.HtmlEncode(title)).Append("\">")
          .Append("<span class=\"md-usage__k\">").Append(label).Append("</span>")
          .Append(Large ? "<div class=\"mat-meter\">" : "<div class=\"mat-meter mat-meter--sm\">")
          .Append("<div class=\"mat-meter__fill\" data-value=\"").Append(pct).Append("\"></div></div>")
          .Append("<span class=\"md-usage__v").Append(percent is null ? " mat-muted" : "").Append("\">").Append(WebUtility.HtmlEncode(value)).Append("</span>")
          .Append("</div>");
    }
}
