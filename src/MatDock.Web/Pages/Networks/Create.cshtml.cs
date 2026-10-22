using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Networks;

public class CreateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;

    public CreateModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<DockerEnvironment> Environments { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed class InputModel
    {
        public long EnvironmentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Driver { get; set; } = "bridge";
        public bool Internal { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Input.EnvironmentId = MatDock.Web.Support.EnvSelection.Current(HttpContext)
            ?? Environments.FirstOrDefault()?.Id ?? 0;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();

        var env = Environments.FirstOrDefault(e => e.Id == Input.EnvironmentId);
        if (env is null)
        {
            ModelState.AddModelError(string.Empty, "Please choose an environment.");
            return Page();
        }

        var (ok, message) = await _connectionService.CreateNetworkAsync(
            _environmentService.BuildSettings(env), Input.Name.Trim(),
            string.IsNullOrWhiteSpace(Input.Driver) ? null : Input.Driver.Trim(), Input.Internal, HttpContext.RequestAborted);

        if (!ok)
        {
            // Shown on this form (the next page is this one again; a TempData message would never be rendered here).
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = false;
        return RedirectToPage("/Networks/Index", new { EnvId = env.Id, Refresh = true });
    }

    private async Task LoadAsync()
        => Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
}
