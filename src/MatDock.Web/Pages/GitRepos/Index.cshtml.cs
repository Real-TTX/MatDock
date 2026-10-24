using MatDock.Core.Entities;
using MatDock.Core.Git;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.GitRepos;

public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<GitRepo>> SortChoices =
        ListView.Sorts<GitRepo>(("Name", r => r.Name), ("Repository", r => r.Url), ("Branch", r => r.Reference));

    private readonly GitRepoService _service;
    private readonly GitCredentialService _credentials;

    public IndexModel(GitRepoService service, GitCredentialService credentials)
    {
        _service = service;
        _credentials = credentials;
    }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortKey { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<GitRepo> Repos { get; private set; } = new();
    public int Total { get; private set; }
    public ListToolbarModel Toolbar { get; private set; } = null!;
    public Dictionary<long, string> CredentialNames { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var all = await _service.GetAllAsync(HttpContext.RequestAborted);
        CredentialNames = (await _credentials.GetAllAsync(HttpContext.RequestAborted))
            .ToDictionary(c => c.Id, c => c.Name);
        Total = all.Count;
        Repos = ListView.Apply(all, Q, SortChoices, SortKey, r => r.Name, r => r.Url, r => r.Reference);
        Toolbar = ListView.Toolbar(Q, SortKey, "Search git repositories", SortChoices);
    }
}
