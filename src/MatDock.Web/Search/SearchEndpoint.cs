using MatDock.Web.Infrastructure;

namespace MatDock.Web.Search;

/// <summary>
/// <c>GET /api/search?q=text&amp;limit=8</c>: the suggestions of the field in the header bar, as MatUi's search field reads them
/// (<c>{ groups: [ { name, items: [ { title, text, href, icon, kind } ] } ] }</c>). Signed in only; the limit is shared fairly among the groups.
/// </summary>
public static class SearchEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext http, GlobalSearch search, string? q, int? limit)
    {
        var take = Math.Clamp(limit ?? 8, 1, 50);
        var groups = await search.SearchAsync(q, http.User.IsAdmin(), take, http.RequestAborted);
        return Results.Json(new
        {
            groups = Share(groups, take).Select(g => new
            {
                name = g.Name,
                items = g.Items.Select(i => new { title = i.Title, text = i.Text ?? string.Empty, href = i.Href, icon = i.Icon, kind = i.Kind }),
            }),
        });
    }

    /// <summary>One of each group in turn until <paramref name="take"/> are chosen, so one group with many hits does not push the others out.</summary>
    public static IReadOnlyList<SearchGroup> Share(IReadOnlyList<SearchGroup> groups, int take)
    {
        var counts = new int[groups.Count];
        for (var chosen = 0; chosen < take;)
        {
            var progressed = false;
            for (var g = 0; g < groups.Count && chosen < take; g++)
            {
                if (counts[g] < groups[g].Items.Count)
                {
                    counts[g]++;
                    chosen++;
                    progressed = true;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        return groups.Select((g, n) => new SearchGroup(g.Name, g.Items.Take(counts[n]).ToList())).Where(g => g.Items.Count > 0).ToList();
    }
}
