namespace MatDock.Web.Controls;

/// <summary>State for the pagination control. Page numbers are 1-based.</summary>
public sealed class PaginationModel
{
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 15;

    public int TotalItems { get; set; }

    /// <summary>Query-string parameter that carries the page number.</summary>
    public string PageParameter { get; set; } = "page";

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    public int FirstItemIndex => TotalItems == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    public int LastItemIndex => Math.Min(Page * PageSize, TotalItems);

    /// <summary>Clamps <see cref="Page"/> into the valid range for the current totals.</summary>
    public void Normalize()
    {
        if (Page < 1)
        {
            Page = 1;
        }

        if (Page > TotalPages)
        {
            Page = TotalPages;
        }
    }
}
