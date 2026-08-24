using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>Shared slot store filled by the child tag helpers and assembled by <see cref="TableFrameTagHelper"/>.</summary>
public sealed class TableFrameContext
{
    public IHtmlContent? Toolbar { get; set; }
    public IHtmlContent? Body { get; set; }
    public IHtmlContent? Pagination { get; set; }
    public IHtmlContent? Actions { get; set; }
}

/// <summary>
/// The standard list-page frame. It encodes the project UI guideline for lists so every list looks
/// the same: a <b>toolbar on top</b> (search/filter/sort), the <b>table</b>, then <b>pagination
/// directly under the table</b>, and finally the <b>list actions</b> (left-aligned buttons, with delete
/// actions spaced apart). Multiple frames can live on one page.
/// <code>
/// &lt;mat-table&gt;
///   &lt;mat-table-toolbar&gt; … search / filters / sort … &lt;/mat-table-toolbar&gt;
///   &lt;mat-table-body&gt; &lt;table&gt; … &lt;/table&gt; &lt;/mat-table-body&gt;
///   &lt;mat-table-pagination&gt; &lt;pagination model="@Model.Pagination" /&gt; &lt;/mat-table-pagination&gt;
///   &lt;mat-table-actions&gt; … buttons … &lt;/mat-table-actions&gt;
/// &lt;/mat-table&gt;
/// </code>
/// </summary>
[HtmlTargetElement("mat-table")]
public sealed class TableFrameTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var frame = new TableFrameContext();
        context.Items[typeof(TableFrameContext)] = frame;

        // Let the child slot tag helpers run and populate the frame.
        await output.GetChildContentAsync();

        output.TagName = "div";
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-table" : $"mat-table {existingClass}");

        var builder = new HtmlContentBuilder();

        if (frame.Toolbar is not null)
        {
            builder.AppendHtml("<div class=\"mat-table__toolbar\">").AppendHtml(frame.Toolbar).AppendHtml("</div>");
        }

        builder.AppendHtml("<div class=\"mat-table__scroll\">");
        if (frame.Body is not null)
        {
            builder.AppendHtml(frame.Body);
        }
        builder.AppendHtml("</div>");

        if (frame.Pagination is not null)
        {
            builder.AppendHtml("<div class=\"mat-table__pagination\">").AppendHtml(frame.Pagination).AppendHtml("</div>");
        }

        if (frame.Actions is not null)
        {
            builder.AppendHtml("<div class=\"mat-table__actions\">").AppendHtml(frame.Actions).AppendHtml("</div>");
        }

        output.Content.SetHtmlContent(builder);
    }
}

/// <summary>Base class for the four table slots; captures its inner content into the parent frame.</summary>
public abstract class TableSlotTagHelper : TagHelper
{
    protected abstract void Assign(TableFrameContext frame, IHtmlContent content);

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var content = await output.GetChildContentAsync();
        if (context.Items.TryGetValue(typeof(TableFrameContext), out var raw) && raw is TableFrameContext frame)
        {
            Assign(frame, content);
        }

        output.SuppressOutput();
    }
}

[HtmlTargetElement("mat-table-toolbar", ParentTag = "mat-table")]
public sealed class TableToolbarTagHelper : TableSlotTagHelper
{
    protected override void Assign(TableFrameContext frame, IHtmlContent content) => frame.Toolbar = content;
}

[HtmlTargetElement("mat-table-body", ParentTag = "mat-table")]
public sealed class TableBodyTagHelper : TableSlotTagHelper
{
    protected override void Assign(TableFrameContext frame, IHtmlContent content) => frame.Body = content;
}

[HtmlTargetElement("mat-table-pagination", ParentTag = "mat-table")]
public sealed class TablePaginationTagHelper : TableSlotTagHelper
{
    protected override void Assign(TableFrameContext frame, IHtmlContent content) => frame.Pagination = content;
}

[HtmlTargetElement("mat-table-actions", ParentTag = "mat-table")]
public sealed class TableActionsTagHelper : TableSlotTagHelper
{
    protected override void Assign(TableFrameContext frame, IHtmlContent content) => frame.Actions = content;
}
