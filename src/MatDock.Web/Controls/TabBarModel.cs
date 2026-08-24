namespace MatDock.Web.Controls;

/// <summary>A single tab in a <see cref="TabBarModel"/>.</summary>
public sealed class TabItem
{
    public required string Title { get; init; }

    public required string Href { get; init; }

    public bool Active { get; init; }

    public string? Icon { get; init; }

    public string? Badge { get; init; }
}

/// <summary>Model for the tab-bar control.</summary>
public sealed class TabBarModel
{
    public List<TabItem> Tabs { get; init; } = new();
}
