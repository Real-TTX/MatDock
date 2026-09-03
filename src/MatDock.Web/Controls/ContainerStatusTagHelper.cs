using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Renders a short container status pill ("Up", "Down", "Error", …) from the Docker state, with the full
/// status text (e.g. "Up 13 days (healthy)") as the hover title.
/// <code>&lt;container-status state="@c.State" status="@c.Status" /&gt;</code>
/// </summary>
[HtmlTargetElement("container-status")]
public sealed partial class ContainerStatusTagHelper : TagHelper
{
    public string? State { get; set; }
    public string? Status { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var (label, cls) = Map(State, Status);

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"pill {cls}");
        if (!string.IsNullOrWhiteSpace(Status))
        {
            output.Attributes.SetAttribute("title", Status);
        }

        output.Content.SetContent(label);
    }

    private static (string Label, string Cls) Map(string? state, string? status)
    {
        var s = (state ?? string.Empty).ToLowerInvariant();
        return s switch
        {
            "running" => (Contains(status, "unhealthy") ? "Unhealthy" : "Up",
                          Contains(status, "unhealthy") ? "pill--offline" : "pill--online"),
            "restarting" => ("Restarting", "pill--offline"),
            "paused" => ("Paused", "pill--offline"),
            "created" => ("Created", "pill--unknown"),
            "removing" => ("Removing", "pill--offline"),
            "dead" => ("Dead", "pill--error"),
            "exited" => ExitCode(status) == 0 ? ("Down", "pill--unknown") : ("Error", "pill--error"),
            "" => ("?", "pill--unknown"),
            _ => (char.ToUpperInvariant(s[0]) + s[1..], "pill--unknown"),
        };
    }

    private static bool Contains(string? text, string token)
        => text is not null && text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static int ExitCode(string? status)
    {
        var m = CodeRegex().Match(status ?? string.Empty);
        return m.Success && int.TryParse(m.Groups[1].Value, out var c) ? c : -1;
    }

    [GeneratedRegex(@"\((\d+)\)")]
    private static partial Regex CodeRegex();
}
