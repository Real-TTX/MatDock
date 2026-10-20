using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// An icon of the MatUi sprite (inlined once per page by the layout): <c>&lt;icon name="save" /&gt;</c> renders
/// <c>&lt;svg class="mat-icon"&gt;&lt;use href="#mat-i-save"&gt;&lt;/svg&gt;</c>. Only names of the sprite exist (a test checks the pages).
/// </summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : TagHelper
{
    /// <summary>The name in the sprite (e.g. "server", "trash").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional accessible label; when omitted the icon is hidden from assistive tech.</summary>
    public string? Title { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (string.IsNullOrEmpty(Name) || !Name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-icon" : $"mat-icon {existingClass}");
        output.Attributes.SetAttribute("focusable", "false");
        if (string.IsNullOrEmpty(Title))
        {
            output.Attributes.SetAttribute("aria-hidden", "true");
            output.Content.SetHtmlContent($"<use href=\"#mat-i-{Name}\"></use>");
        }
        else
        {
            output.Attributes.SetAttribute("role", "img");
            output.Attributes.SetAttribute("aria-label", Title);
            output.Content.SetHtmlContent($"<title>{System.Net.WebUtility.HtmlEncode(Title)}</title><use href=\"#mat-i-{Name}\"></use>");
        }
    }
}
