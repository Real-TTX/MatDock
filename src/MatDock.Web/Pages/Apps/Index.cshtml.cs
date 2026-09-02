using MatDock.Core.Entities;
using MatDock.Core.Templates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Apps;

public class IndexModel : PageModel
{
    private readonly StackTemplateService _templateService;

    public IndexModel(StackTemplateService templateService)
    {
        _templateService = templateService;
    }

    public List<StackTemplate> Templates { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Templates = await _templateService.GetAllAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _templateService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Vorlage gelöscht." : "Vorlage nicht gefunden.";
        IsError = !deleted;
        return RedirectToPage();
    }
}
