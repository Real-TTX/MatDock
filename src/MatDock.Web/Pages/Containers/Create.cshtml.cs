using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

/// <summary>Starts a one-off container (<c>docker run -d</c>) from a form. Not managed: use a stack for anything that should stay.</summary>
public class CreateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly ContainerService _containerService;

    public CreateModel(EnvironmentService environmentService, ContainerService containerService)
    {
        _environmentService = environmentService;
        _containerService = containerService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<DockerEnvironment> Environments { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed class InputModel
    {
        public long EnvironmentId { get; set; }
        public string Image { get; set; } = string.Empty;
        public string? Name { get; set; }

        /// <summary>One mapping per line.</summary>
        public string? Ports { get; set; }

        /// <summary>One KEY=value per line.</summary>
        public string? Env { get; set; }

        /// <summary>One mount per line.</summary>
        public string? Volumes { get; set; }

        public string Restart { get; set; } = "unless-stopped";
        public string? Network { get; set; }
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

        var spec = new ContainerRunSpec
        {
            Image = (Input.Image ?? string.Empty).Trim(),
            Name = string.IsNullOrWhiteSpace(Input.Name) ? null : Input.Name.Trim(),
            Ports = Lines(Input.Ports),
            Env = Lines(Input.Env),
            Volumes = Lines(Input.Volumes),
            Restart = string.IsNullOrWhiteSpace(Input.Restart) ? "no" : Input.Restart.Trim(),
            Network = string.IsNullOrWhiteSpace(Input.Network) ? null : Input.Network.Trim(),
        };

        var invalid = ContainerRunCommands.Validate(spec);
        if (invalid is not null)
        {
            ModelState.AddModelError(string.Empty, invalid);
            return Page();
        }

        var (ok, message) = await _containerService.RunAsync(env, spec, HttpContext.RequestAborted);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = false;
        return RedirectToPage("/Containers/Index");
    }

    private static List<string> Lines(string? text)
        => (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async Task LoadAsync()
        => Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
}
