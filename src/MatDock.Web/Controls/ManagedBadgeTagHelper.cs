using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Marks an object that another program created and maintains through the API (its <c>ManagedBy</c> value),
/// e.g. "Managed by Mattiverse". Renders nothing when the value is empty.
/// <code>&lt;managed-badge value="@stack.ManagedBy" /&gt;</code>
/// </summary>
[HtmlTargetElement("managed-badge", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ManagedBadgeTagHelper : TagHelper
{
    public string? Value { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var owner = DisplayName(Value);
        if (owner is null)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-pill mat-pill--info");
        output.Attributes.SetAttribute("title", $"Created by {owner}. Manual changes may be overwritten.");
        output.Content.SetContent($"Managed by {owner}");
    }

    /// <summary>The owner as shown to the user ("mattiverse" becomes "Mattiverse"), or null when not managed.</summary>
    public static string? DisplayName(string? managedBy)
    {
        if (string.IsNullOrWhiteSpace(managedBy))
        {
            return null;
        }

        var value = managedBy.Trim();
        return string.Equals(value, "mattiverse", StringComparison.OrdinalIgnoreCase) ? "Mattiverse" : value;
    }
}
