using MatDock.Core.Containers;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Terminal;

public class IndexModel : PageModel
{
    private readonly EnvironmentService _environmentService;

    public IndexModel(EnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    public long EnvId { get; private set; }
    public string? Container { get; private set; }
    public string EnvName { get; private set; } = string.Empty;
    public string Title { get; private set; } = "Terminal";

    public async Task<IActionResult> OnGetAsync(long envId, string? container, string? name)
    {
        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return RedirectToPage("/Environments/Index");
        }

        // A container id is validated the same way the endpoint validates it; reject malformed values.
        if (!string.IsNullOrEmpty(container) && !ContainerCommands.IsValidId(container))
        {
            return RedirectToPage("/Environments/Index");
        }

        EnvId = envId;
        EnvName = env.Name;
        Container = string.IsNullOrEmpty(container) ? null : container;
        Title = Container is null
            ? $"Shell · {env.Name}"
            : $"Konsole · {(string.IsNullOrEmpty(name) ? Container : name)}";

        return Page();
    }
}
