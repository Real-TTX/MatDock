using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class UpdateModel : PageModel
{
    private readonly UpdateService _updates;
    private readonly StackService _stacks;
    private readonly EnvironmentService _environments;

    public UpdateModel(UpdateService updates, StackService stacks, EnvironmentService environments)
    {
        _updates = updates;
        _stacks = stacks;
        _environments = environments;
    }

    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public bool PreBackup { get; set; } = true;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public Stack? Stack { get; private set; }
    public string? EnvName { get; private set; }
    public StackUpdateReport? Report { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Stack = await _stacks.GetAsync(Id, ct);
        if (Stack is null)
        {
            return RedirectToPage("Index");
        }

        var env = await _environments.GetAsync(Stack.EnvironmentId, ct);
        if (env is null)
        {
            Report = StackUpdateReport.Failed("Environment not available.");
            return Page();
        }

        EnvName = env.Name;
        Report = await _updates.CheckStackAsync(env, Stack.Name, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        var (ok, message) = await _updates.UpdateStackAsync(Id, PreBackup, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { Id });
    }
}
