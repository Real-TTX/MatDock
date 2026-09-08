using System.Text;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Renders pagination directly under a table: a "x–y of z" summary plus previous/next and numbered
/// page links. Every link preserves the current query string (search, sort, …) and only swaps the
/// page parameter. Usage: <c>&lt;pagination model="@Model.Pagination" /&gt;</c>.
/// </summary>
[HtmlTargetElement("pagination", TagStructure = TagStructure.WithoutEndTag)]
public sealed class PaginationTagHelper : TagHelper
{
    public PaginationModel? Model { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Model is null)
        {
            output.SuppressOutput();
            return;
        }

        Model.Normalize();

        // Nothing to page: hide the whole control (no summary-only band before the table footer).
        if (Model.TotalItems == 0 || Model.TotalPages <= 1)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "nav";
        output.Attributes.SetAttribute("class", "mat-pagination");
        output.Attributes.SetAttribute("aria-label", "Pagination");

        var builder = new HtmlContentBuilder();
        builder.AppendHtml($"<span class=\"mat-pagination__summary\">{Model.FirstItemIndex}–{Model.LastItemIndex} von {Model.TotalItems}</span>");

        if (Model.TotalPages > 1)
        {
            builder.AppendHtml("<ul class=\"mat-pagination__pages\">");
            builder.AppendHtml(PageLink(Model.Page - 1, "‹", Model.HasPrevious, "Previous page"));

            foreach (var page in PageNumbers(Model.Page, Model.TotalPages))
            {
                if (page < 0)
                {
                    builder.AppendHtml("<li class=\"mat-pagination__gap\">…</li>");
                }
                else
                {
                    builder.AppendHtml(PageLink(page, page.ToString(), enabled: true, $"Page {page}", isCurrent: page == Model.Page));
                }
            }

            builder.AppendHtml(PageLink(Model.Page + 1, "›", Model.HasNext, "Next page"));
            builder.AppendHtml("</ul>");
        }

        output.Content.SetHtmlContent(builder);
    }

    private static IEnumerable<int> PageNumbers(int current, int total)
    {
        const int window = 1; // pages on each side of the current page
        var pages = new List<int>();
        for (var p = 1; p <= total; p++)
        {
            if (p == 1 || p == total || (p >= current - window && p <= current + window))
            {
                pages.Add(p);
            }
        }

        var result = new List<int>();
        var previous = 0;
        foreach (var p in pages)
        {
            if (previous != 0 && p - previous > 1)
            {
                result.Add(-1); // gap marker
            }

            result.Add(p);
            previous = p;
        }

        return result;
    }

    private IHtmlContent PageLink(int page, string label, bool enabled, string ariaLabel, bool isCurrent = false)
    {
        var css = "mat-pagination__page";
        if (isCurrent)
        {
            css += " is-current";
        }

        if (!enabled)
        {
            return new HtmlString($"<li class=\"{css} is-disabled\"><span aria-hidden=\"true\">{label}</span></li>");
        }

        var href = BuildUrl(page);
        var current = isCurrent ? " aria-current=\"page\"" : string.Empty;
        return new HtmlString($"<li class=\"{css}\"><a href=\"{href}\" aria-label=\"{ariaLabel}\"{current}>{label}</a></li>");
    }

    private string BuildUrl(int page)
    {
        var request = ViewContext.HttpContext.Request;
        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Query)
        {
            query[pair.Key] = pair.Value.ToString();
        }

        query[Model!.PageParameter] = page.ToString();

        var sb = new StringBuilder(request.Path);
        var first = true;
        foreach (var pair in query)
        {
            if (string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            sb.Append(first ? '?' : '&');
            sb.Append(Uri.EscapeDataString(pair.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(pair.Value));
            first = false;
        }

        return sb.ToString();
    }
}
