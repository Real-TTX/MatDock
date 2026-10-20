using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// The button row at the end of a form (MatUi <c>mat-btn-row</c>): positive to negative, the primary action first; a
/// <c>&lt;span class="mat-btn-row__spacer"&gt;&lt;/span&gt;</c> pushes destructive buttons to the end.
/// </summary>
[HtmlTargetElement("mat-form-buttons")]
public sealed class FormButtonsTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();

        output.TagName = "div";
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-btn-row" : $"mat-btn-row {existingClass}");
        output.Content.SetHtmlContent(child);
    }
}
