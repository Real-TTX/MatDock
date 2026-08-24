using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// The standard button row for forms and lists. Renders its children in one line. Following the
/// project guideline (positive → negative: Save, Back, &lt;space&gt; Delete), place a
/// <c>&lt;span class="mat-buttons__spacer"&gt;&lt;/span&gt;</c> before destructive buttons to push them apart.
/// </summary>
[HtmlTargetElement("mat-form-buttons")]
public sealed class FormButtonsTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();

        output.TagName = "div";
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-buttons" : $"mat-buttons {existingClass}");
        output.Content.SetHtmlContent(child);
    }
}
