using MatDock.Core.Entities;
using MatDock.Core.Git;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.GitCredentials;

public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<GitCredential>> SortChoices =
        ListView.Sorts<GitCredential>(("Name", c => c.Name), ("Type", c => c.AuthType.ToString()), ("User", c => c.Username));

    private readonly GitCredentialService _service;

    public IndexModel(GitCredentialService service)
    {
        _service = service;
    }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortKey { get; set; }

    public List<GitCredential> Credentials { get; private set; } = new();
    public int Total { get; private set; }
    public ListToolbarModel Toolbar { get; private set; } = null!;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        var all = await _service.GetAllAsync(HttpContext.RequestAborted);
        Total = all.Count;
        Credentials = ListView.Apply(all, Q, SortChoices, SortKey, c => c.Name, c => c.Username, c => c.AuthType.ToString());
        Toolbar = ListView.Toolbar(Q, SortKey, "Search git credentials", SortChoices);
    }
}
