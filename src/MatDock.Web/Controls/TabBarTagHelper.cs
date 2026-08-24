using System.Net;
using System.Text;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Renders a horizontal tab bar from a <see cref="TabBarModel"/>. Each tab is a link, so tabs work
/// without JavaScript and can be deep-linked. Usage: <c>&lt;tab-bar model="@Model.Tabs" /&gt;</c>.
/// </summary>
[HtmlTargetElement("tab-bar", TagStructure = TagStructure.WithoutEndTag)]
public sealed class TabBarTagHelper : TagHelper
{
    public TabBarModel? Model { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Model is null || Model.Tabs.Count == 0)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "nav";
        output.Attributes.SetAttribute("class", "mat-tabs");

        var sb = new StringBuilder();
        foreach (var tab in Model.Tabs)
        {
            sb.Append("<a class=\"mat-tabs__tab");
            if (tab.Active)
            {
                sb.Append(" is-active");
            }

            sb.Append("\" href=\"").Append(WebUtility.HtmlEncode(tab.Href)).Append('"');
            if (tab.Active)
            {
                sb.Append(" aria-current=\"page\"");
            }

            sb.Append('>');

            if (!string.IsNullOrEmpty(tab.Icon))
            {
                sb.Append(Icons.Svg(tab.Icon));
            }

            sb.Append("<span>").Append(WebUtility.HtmlEncode(tab.Title)).Append("</span>");

            if (!string.IsNullOrEmpty(tab.Badge))
            {
                sb.Append("<span class=\"mat-badge\">").Append(WebUtility.HtmlEncode(tab.Badge)).Append("</span>");
            }

            sb.Append("</a>");
        }

        output.Content.SetHtmlContent(sb.ToString());
    }
}
