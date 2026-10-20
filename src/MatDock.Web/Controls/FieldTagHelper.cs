using System.Net;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// A field of a form as the toolkit has it (rules FORM-03, FORM-06): the label above, the control (the child content), the hint below and the
/// reserved line for the error directly under it. The message of the server (model validation) lands in that line, and the toolkit's script
/// writes its own there. Keeps every form row identical without repeating markup.
/// <code>
/// &lt;mat-field label="Name" asp-for="Input.Name" hint="Display name" required="true"&gt;
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

    /// <summary>Marks the field as required with an asterisk in the label (FORM-06).</summary>
    public bool Required { get; set; }

    /// <summary>The field spans both columns of a two-column form (FORM-07): descriptions, long values.</summary>
    public bool Full { get; set; }

    /// <summary>The id of the control, for a field without <c>asp-for</c>: the label points to it (FORM-08).</summary>
    [HtmlAttributeName("for-id")]
    public string? ForId { get; set; }

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
        var invalid = For is not null && ViewContext.ViewData.ModelState.TryGetValue(For.Name, out var entry) && entry.Errors.Count > 0;
        var classes = new List<string> { "mat-field" };
        if (Full)
        {
            classes.Add("mat-field--full");
        }

        if (invalid)
        {
            classes.Add("is-invalid");
        }

        if (output.Attributes["class"]?.Value?.ToString() is { Length: > 0 } existing)
        {
            classes.Add(existing);
        }

        output.Attributes.SetAttribute("class", string.Join(' ', classes));

        var builder = new HtmlContentBuilder();

        if (!string.IsNullOrEmpty(Label))
        {
            var forId = ForId ?? (For is null ? null : TagBuilder.CreateSanitizedId(For.Name, "_"));
            var forAttr = forId is null ? string.Empty : $" for=\"{WebUtility.HtmlEncode(forId)}\"";
            var star = Required ? " <span class=\"mat-req\" aria-hidden=\"true\">*</span>" : string.Empty;
            builder.AppendHtml($"<label class=\"mat-label\"{forAttr}>{WebUtility.HtmlEncode(Label)}{star}</label>");
        }

        builder.AppendHtml(child);

        if (!string.IsNullOrEmpty(Hint))
        {
            builder.AppendHtml($"<span class=\"mat-hint\">{WebUtility.HtmlEncode(Hint)}</span>");
        }

        var validation = For is null
            ? null
            : _generator.GenerateValidationMessage(ViewContext, For.ModelExplorer, For.Name, message: null, tag: "span", htmlAttributes: new { @class = "mat-error", aria_live = "polite" });
        if (validation is not null)
        {
            builder.AppendHtml(validation);
        }
        else
        {
            builder.AppendHtml("<span class=\"mat-error\" aria-live=\"polite\"></span>");
        }

        output.Content.SetHtmlContent(builder);
    }
}
