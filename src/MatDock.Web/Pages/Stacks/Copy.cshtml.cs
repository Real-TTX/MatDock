using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

/// <summary>Copies a stack into another environment (or the same one under another name), optionally deploying the copy. Volumes are not copied.</summary>
public class CopyModel : PageModel
{
    private readonly StackService _stacks;
    private readonly EnvironmentService _environments;

    public CopyModel(StackService stacks, EnvironmentService environments)
    {
        _stacks = stacks;
        _environments = environments;
    }

    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public long TargetEnvironmentId { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public bool Deploy { get; set; }

    public Stack? Source { get; private set; }
    public List<DockerEnvironment> Environments { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Name = Source!.Name;
        TargetEnvironmentId = Environments.FirstOrDefault(e => e.Id != Source.EnvironmentId)?.Id ?? Source.EnvironmentId;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var (ok, message, copyId) = await _stacks.CopyAsync(Id, TargetEnvironmentId, Name, Deploy, HttpContext.RequestAborted);
        if (copyId == 0)
        {
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = !ok;
        return Redirect($"/Stacks?EnvId={TargetEnvironmentId}");
    }

    private async Task<bool> LoadAsync()
    {
        Source = await _stacks.GetAsync(Id, HttpContext.RequestAborted);
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        return Source is not null && Environments.Any(e => e.Id == Source.EnvironmentId);
    }
}
