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
        var (label, cls, icon) = Map(State, Status);

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"pill {cls}");
        if (!string.IsNullOrWhiteSpace(Status))
        {
            output.Attributes.SetAttribute("title", Status);
        }

        var labelHtml = System.Net.WebUtility.HtmlEncode(label);
        output.Content.SetHtmlContent(string.IsNullOrEmpty(icon) ? labelHtml : $"{IconSvg(icon)} {labelHtml}");
    }

    // Same status vocabulary as the Stacks list badge: play = running, pause = stopped/down, alert = problem.
    private static (string Label, string Cls, string Icon) Map(string? state, string? status)
    {
        var s = (state ?? string.Empty).ToLowerInvariant();
        return s switch
        {
            "running" => Contains(status, "unhealthy")
                ? ("Unhealthy", "pill--offline", "alert")
                : ("Up", "pill--online", "play"),
            "restarting" => ("Restarting", "pill--offline", "alert"),
            "paused" => ("Paused", "pill--offline", "pause"),
            "created" => ("Created", "pill--unknown", "pause"),
            "removing" => ("Removing", "pill--offline", "alert"),
            "dead" => ("Dead", "pill--error", "alert"),
            "exited" => ExitCode(status) == 0 ? ("Down", "pill--unknown", "pause") : ("Error", "pill--error", "alert"),
            "" => ("?", "pill--unknown", ""),
            _ => (char.ToUpperInvariant(s[0]) + s[1..], "pill--unknown", ""),
        };
    }

    // Inline SVG matching <icon>: 1em, currentColor stroke — so the pill icon inherits the badge colour.
    private static string IconSvg(string name)
    {
        if (!Icons.TryGet(name, out var inner))
        {
            return string.Empty;
        }

        return "<svg class=\"mat-icon\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" " +
               "stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" width=\"1em\" height=\"1em\" " +
               $"aria-hidden=\"true\" focusable=\"false\">{inner}</svg>";
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
