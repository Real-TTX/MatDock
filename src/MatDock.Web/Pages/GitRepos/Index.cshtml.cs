using MatDock.Core.Entities;
using MatDock.Core.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.GitRepos;

public class IndexModel : PageModel
{
    private readonly GitRepoService _service;
    private readonly GitCredentialService _credentials;

    public IndexModel(GitRepoService service, GitCredentialService credentials)
    {
        _service = service;
        _credentials = credentials;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<GitRepo> Repos { get; private set; } = new();
    public Dictionary<long, string> CredentialNames { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Repos = await _service.GetAllAsync(HttpContext.RequestAborted);
        CredentialNames = (await _credentials.GetAllAsync(HttpContext.RequestAborted))
            .ToDictionary(c => c.Id, c => c.Name);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _service.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "Git repository deleted.";
        return RedirectToPage();
    }
}
