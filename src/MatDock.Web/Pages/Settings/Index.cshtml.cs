using MatDock.Core.Backups;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

public class IndexModel : PageModel
{
    private readonly BackupTargetService _targetService;
    private readonly AppPaths _paths;

    public IndexModel(BackupTargetService targetService, AppPaths paths)
    {
        _targetService = targetService;
        _paths = paths;
    }

    public List<BackupTarget> Targets { get; private set; } = new();
    public string LocalPath => _paths.BackupsPath;
    public bool LocalIsDefault => !Targets.Any(t => t.IsDefault);

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Targets = await _targetService.GetAllAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostTestAsync(long id)
    {
        var target = await _targetService.GetAsync(id, HttpContext.RequestAborted);
        if (target is null)
        {
            StatusMessage = "Ziel nicht gefunden.";
            IsError = true;
            return RedirectToPage();
        }

        var (ok, message) = await _targetService.TestAsync(target, HttpContext.RequestAborted);
        StatusMessage = $"{target.Name}: {message}";
        IsError = !ok;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDefaultAsync(long id)
    {
        await _targetService.SetDefaultAsync(id, HttpContext.RequestAborted);
        StatusMessage = "Standard-Ziel gesetzt.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLocalDefaultAsync()
    {
        await _targetService.SetDefaultAsync(null, HttpContext.RequestAborted);
        StatusMessage = "Lokaler Speicher ist jetzt Standard.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _targetService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Ziel gelöscht." : "Ziel nicht gefunden.";
        IsError = !deleted;
        return RedirectToPage();
    }
}
