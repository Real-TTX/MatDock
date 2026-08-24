using System.Net;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// A consistent form field: label + the input (supplied as child content) + optional hint + the
/// server-side validation message. Keeps every form row identical without repeating markup.
/// <code>
/// &lt;mat-field label="Name" asp-for="Input.Name" hint="Anzeigename"&gt;
///     &lt;input asp-for="Input.Name" class="mat-input" /&gt;
/// &lt;/mat-field&gt;
/// </code>
/// </summary>
[HtmlTargetElement("mat-field")]
public sealed class FieldTagHelper : TagHelper
{
    private readonly IHtmlGenerator _generator;

    public FieldTagHelper(IHtmlGenerator generator)
    {
        _generator = generator;
    }

    public string? Label { get; set; }

    public string? Hint { get; set; }

    /// <summary>Bind with <c>asp-for</c> to render the label's <c>for</c> and the validation message.</summary>
    [HtmlAttributeName("asp-for")]
    public ModelExpression? For { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();

        output.TagName = "div";
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-field" : $"mat-field {existingClass}");

        var builder = new HtmlContentBuilder();

        if (!string.IsNullOrEmpty(Label))
        {
            var forId = For is not null ? TagBuilder.CreateSanitizedId(For.Name, "_") : null;
            var forAttr = forId is null ? string.Empty : $" for=\"{forId}\"";
            builder.AppendHtml($"<label class=\"mat-label\"{forAttr}>{WebUtility.HtmlEncode(Label)}</label>");
        }

        builder.AppendHtml("<div class=\"mat-field__control\">").AppendHtml(child).AppendHtml("</div>");

        if (!string.IsNullOrEmpty(Hint))
        {
            builder.AppendHtml($"<span class=\"mat-hint\">{WebUtility.HtmlEncode(Hint)}</span>");
        }

        if (For is not null)
        {
            var validation = _generator.GenerateValidationMessage(
                ViewContext,
                For.ModelExplorer,
                For.Name,
                message: null,
                tag: "span",
                htmlAttributes: new { @class = "mat-validation" });

            if (validation is not null)
            {
                builder.AppendHtml(validation);
            }
        }

        output.Content.SetHtmlContent(builder);
    }
}
