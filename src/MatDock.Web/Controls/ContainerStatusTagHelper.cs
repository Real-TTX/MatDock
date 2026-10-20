using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Renders a short container status pill ("Up", "Down", "Error", …) from the Docker state, with the full
/// status text (e.g. "Up 13 days (healthy)") as the hover title. The MatUi pill draws its dot in the colour of the state.
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
        output.Attributes.SetAttribute("class", $"mat-pill {cls}");
        if (!string.IsNullOrWhiteSpace(Status))
        {
            output.Attributes.SetAttribute("title", Status);
        }

        output.Content.SetContent(label);
    }

    // Same status vocabulary as the Stacks list: ok = running, warn = needs a look, error = failed, plain = stopped.
    private static (string Label, string Cls) Map(string? state, string? status)
    {
        var s = (state ?? string.Empty).ToLowerInvariant();
        return s switch
        {
            "running" => Contains(status, "unhealthy")
                ? ("Unhealthy", "mat-pill--warn")
                : ("Up", "mat-pill--ok"),
            "restarting" => ("Restarting", "mat-pill--warn"),
            "paused" => ("Paused", "mat-pill--warn"),
            "created" => ("Created", "mat-pill--plain"),
            "removing" => ("Removing", "mat-pill--warn"),
            "dead" => ("Dead", "mat-pill--error"),
            "exited" => ExitCode(status) == 0 ? ("Down", "mat-pill--plain") : ("Error", "mat-pill--error"),
            "" => ("?", "mat-pill--plain"),
            _ => (char.ToUpperInvariant(s[0]) + s[1..], "mat-pill--plain"),
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
