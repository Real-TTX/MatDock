using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class IndexModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;

    public IndexModel(StackService stackService, EnvironmentService environmentService)
    {
        _stackService = stackService;
        _environmentService = environmentService;
    }

    public List<Stack> Stacks { get; private set; } = new();
    public Dictionary<long, string> EnvNames { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    [TempData] public string? Output { get; set; }

    public async Task OnGetAsync()
    {
        Stacks = await _stackService.GetAllAsync(HttpContext.RequestAborted);
        EnvNames = (await _environmentService.GetAllAsync(HttpContext.RequestAborted)).ToDictionary(e => e.Id, e => e.Name);
    }

    public async Task<IActionResult> OnPostDeployAsync(long id)
    {
        var (ok, output) = await _stackService.DeployAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack deployt." : "Deploy fehlgeschlagen.";
        IsError = !ok;
        Output = output;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDownAsync(long id)
    {
        var (ok, output) = await _stackService.DownAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack gestoppt." : "Stoppen fehlgeschlagen.";
        IsError = !ok;
        Output = output;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _stackService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Stack gelöscht (Container bleiben ggf. laufend – vorher stoppen)." : "Stack nicht gefunden.";
        IsError = !deleted;
        return RedirectToPage();
    }
}
