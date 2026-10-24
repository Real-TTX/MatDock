using MatDock.Web.Infrastructure;
using MatDock.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Search;

/// <summary>All results of a search of the header bar (the last row of its suggestions leads here), by group.</summary>
public class IndexModel : PageModel
{
    private readonly GlobalSearch _search;

    public IndexModel(GlobalSearch search) => _search = search;

    [BindProperty(SupportsGet = true, Name = "q")] public string? Q { get; set; }

    public IReadOnlyList<SearchGroup> Groups { get; private set; } = Array.Empty<SearchGroup>();

    public async Task OnGetAsync()
    {
        Q = Q?.Trim();
        if (!string.IsNullOrEmpty(Q))
        {
            Groups = await _search.SearchAsync(Q.Length > 100 ? Q[..100] : Q, User.IsAdmin(), 50, HttpContext.RequestAborted);
        }
    }
}
