using MatDock.Core.Entities;
using MatDock.Core.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.GitCredentials;

public class IndexModel : PageModel
{
    private readonly GitCredentialService _service;

    public IndexModel(GitCredentialService service)
    {
        _service = service;
    }

    public List<GitCredential> Credentials { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Credentials = await _service.GetAllAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _service.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Git credential deleted." : "Git credential not found.";
        IsError = !deleted;
        return RedirectToPage();
    }
}
