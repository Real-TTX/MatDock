namespace MatDock.Web.Support;

/// <summary>What the shared toolbar of a list (<c>_ListToolbar</c>) shows: the search text, the chosen sort and the sorts to choose from.</summary>
public sealed record ListToolbarModel(string? Q, string SortKey, string SearchLabel, IReadOnlyList<(string Key, string Label)> Sorts);

/// <summary>One way to sort a list: its key (in the address), the text in the sort menu and what the items are compared by.</summary>
public sealed record SortChoice<T>(string Key, string Label, Func<T, IComparable?> Value, bool Descending);

/// <summary>Search and sort of a short list that is read completely (credentials, repositories, jobs): the same behaviour on every such page.</summary>
public static class ListView
{
    /// <summary>The sorts of a list: for each field "A–Z" and "Z–A" (keys <c>NameAsc</c> and <c>NameDesc</c>), in the order given.</summary>
    public static IReadOnlyList<SortChoice<T>> Sorts<T>(params (string Field, Func<T, IComparable?> Value)[] fields)
        => fields.SelectMany(f => new[]
            {
                new SortChoice<T>(f.Field + "Asc", "A–Z: " + f.Field, f.Value, Descending: false),
                new SortChoice<T>(f.Field + "Desc", "Z–A: " + f.Field, f.Value, Descending: true),
            })
            .ToList();

    /// <summary>The items that contain the search text (case ignored) in one of the fields, in the chosen sort (the first one when the key is unknown).</summary>
    public static List<T> Apply<T>(IEnumerable<T> items, string? search, IReadOnlyList<SortChoice<T>> sorts, string? sortKey, params Func<T, string?>[] searchFields)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            items = items.Where(item => searchFields.Any(field => field(item)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true));
        }

        var sort = sorts.FirstOrDefault(s => s.Key == sortKey) ?? sorts[0];
        return (sort.Descending ? items.OrderByDescending(sort.Value, Order) : items.OrderBy(sort.Value, Order)).ToList();
    }

    public static ListToolbarModel Toolbar<T>(string? q, string? sortKey, string searchLabel, IReadOnlyList<SortChoice<T>> sorts)
        => new(q, sorts.Any(s => s.Key == sortKey) ? sortKey! : sorts[0].Key, searchLabel, sorts.Select(s => (s.Key, s.Label)).ToList());

    private static readonly IComparer<IComparable?> Order = Comparer<IComparable?>.Create((a, b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        _ => a.CompareTo(b),
    });
}
