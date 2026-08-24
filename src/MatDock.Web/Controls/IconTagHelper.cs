using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Renders an inline SVG icon: <c>&lt;icon name="save" /&gt;</c>. Size and colour follow CSS
/// (<c>width/height: 1em</c>, <c>stroke: currentColor</c>) so icons sit naturally inside buttons and text.
/// </summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : TagHelper
{
    /// <summary>Icon key from <see cref="Icons"/> (e.g. "server", "trash").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional accessible label; when omitted the icon is hidden from assistive tech.</summary>
    public string? Title { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!Icons.TryGet(Name, out var inner))
        {
            // Unknown icon: render nothing rather than a broken element.
            output.SuppressOutput();
            return;
        }

        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;

        var existingClass = output.Attributes["class"]?.Value?.ToString();
        var cssClass = string.IsNullOrEmpty(existingClass) ? "mat-icon" : $"mat-icon {existingClass}";
        output.Attributes.SetAttribute("class", cssClass);
        output.Attributes.SetAttribute("viewBox", "0 0 24 24");
        output.Attributes.SetAttribute("fill", "none");
        output.Attributes.SetAttribute("stroke", "currentColor");
        output.Attributes.SetAttribute("stroke-width", "2");
        output.Attributes.SetAttribute("stroke-linecap", "round");
        output.Attributes.SetAttribute("stroke-linejoin", "round");
        output.Attributes.SetAttribute("width", "1em");
        output.Attributes.SetAttribute("height", "1em");

        if (string.IsNullOrEmpty(Title))
        {
            output.Attributes.SetAttribute("aria-hidden", "true");
            output.Attributes.SetAttribute("focusable", "false");
            output.Content.SetHtmlContent(inner);
        }
        else
        {
            output.Attributes.SetAttribute("role", "img");
            output.Attributes.SetAttribute("aria-label", Title);
            output.Content.SetHtmlContent($"<title>{System.Net.WebUtility.HtmlEncode(Title)}</title>{inner}");
        }
    }
}
