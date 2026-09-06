using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Launchpad;

/// <summary>App launchpad: managed stacks that carry MatDock app metadata, as a tile grid grouped by category.</summary>
public class IndexModel : PageModel
{
    private readonly AppLaunchpadService _launchpad;

    public IndexModel(AppLaunchpadService launchpad) => _launchpad = launchpad;

    public long EnvId { get; private set; }
    public List<CategoryGroup> Groups { get; private set; } = new();
    public int TotalApps { get; private set; }

    public sealed record CategoryGroup(string Category, List<AppTile> Apps);

    public async Task OnGetAsync()
    {
        EnvId = EnvSelection.Resolve(HttpContext) ?? 0;

        var tiles = await _launchpad.GetTilesAsync(EnvId > 0 ? EnvId : null, HttpContext.RequestAborted);
        TotalApps = tiles.Count;
        Groups = tiles
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Meta.Category) ? "Apps" : t.Meta.Category!)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryGroup(g.Key, g.ToList()))
            .ToList();
    }
}
