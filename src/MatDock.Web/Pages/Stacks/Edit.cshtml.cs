using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class EditModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;

    public EditModel(StackService stackService, EnvironmentService environmentService)
    {
        _stackService = stackService;
        _environmentService = environmentService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public List<DockerEnvironment> Environments { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    public string? DeployOutput { get; private set; }

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Name ist erforderlich.")]
        [RegularExpression(@"^[a-z0-9][a-z0-9_-]{0,62}\z", ErrorMessage = "Nur Kleinbuchstaben, Ziffern, _ und -, Beginn alphanumerisch (max. 63 Zeichen).")]
        public string Name { get; set; } = string.Empty;

        [Range(1, long.MaxValue, ErrorMessage = "Environment ist erforderlich.")]
        public long EnvironmentId { get; set; }

        [Required(ErrorMessage = "Compose-YAML ist erforderlich.")]
        public string ComposeYaml { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        await LoadEnvironmentsAsync();

        if (id is > 0)
        {
            var stack = await _stackService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (stack is null)
            {
                return RedirectToPage("Index");
            }

            Input = new InputModel
            {
                Id = stack.Id,
                Name = stack.Name,
                EnvironmentId = stack.EnvironmentId,
                ComposeYaml = stack.ComposeYaml,
            };
        }
        else if (Input.ComposeYaml.Length == 0)
        {
            Input.ComposeYaml = "services:\n  app:\n    image: nginx:alpine\n    restart: unless-stopped\n    ports:\n      - \"8080:80\"\n";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadEnvironmentsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await SaveAsync();
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return Page();
        }

        StatusMessage = result.Message;
        IsError = false;
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostSaveDeployAsync()
    {
        await LoadEnvironmentsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await SaveAsync();
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return Page();
        }

        var (ok, output) = await _stackService.DeployAsync(result.Id, HttpContext.RequestAborted);
        Input.Id = result.Id;
        DeployOutput = output;
        StatusMessage = ok ? "Stack gespeichert & deployt." : "Gespeichert, aber Deploy fehlgeschlagen.";
        IsError = !ok;
        return Page();
    }

    private async Task<(bool Ok, string Message, long Id)> SaveAsync()
    {
        var input = new StackInput
        {
            Name = Input.Name,
            EnvironmentId = Input.EnvironmentId,
            ComposeYaml = Input.ComposeYaml,
        };

        if (IsEdit)
        {
            var (ok, message) = await _stackService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            return (ok, message, Input.Id!.Value);
        }

        return await _stackService.CreateAsync(input, HttpContext.RequestAborted);
    }

    private async Task LoadEnvironmentsAsync()
        => Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
}
