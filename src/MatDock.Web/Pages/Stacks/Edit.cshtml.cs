using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Git;
using MatDock.Core.Stacks;
using MatDock.Core.Templates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class EditModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;
    private readonly StackTemplateService _templateService;
    private readonly GitCredentialService _gitCredentialService;

    public EditModel(
        StackService stackService,
        EnvironmentService environmentService,
        StackTemplateService templateService,
        GitCredentialService gitCredentialService)
    {
        _stackService = stackService;
        _environmentService = environmentService;
        _templateService = templateService;
        _gitCredentialService = gitCredentialService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<GitCredential> GitCredentials { get; private set; } = new();
    public string? FromTemplateName { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    public string? DeployOutput { get; private set; }

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Name is required.")]
        [RegularExpression(@"^[a-z0-9][a-z0-9_-]{0,62}\z", ErrorMessage = "Only lowercase letters, digits, _ and -, must start alphanumeric (max. 63 characters).")]
        public string Name { get; set; } = string.Empty;

        [Range(1, long.MaxValue, ErrorMessage = "Environment is required.")]
        public long EnvironmentId { get; set; }

        /// <summary>"inline" (compose editor) or "git" (clone a repository).</summary>
        public string Source { get; set; } = "inline";

        // Inline source. Nullable so a git-backed stack (empty editor) doesn't trip the implicit
        // "non-nullable reference type is required" model validation; ValidateSource enforces it for inline.
        public string? ComposeYaml { get; set; }

        // Git source
        public string? GitRepoUrl { get; set; }
        public string? GitReference { get; set; }
        public string? GitComposePath { get; set; }
        public long? GitCredentialId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id, long? templateId)
    {
        await LoadListsAsync();

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
                Source = stack.IsGitBacked ? "git" : "inline",
                ComposeYaml = stack.ComposeYaml,
                GitRepoUrl = stack.GitRepoUrl,
                GitReference = stack.GitReference,
                GitComposePath = stack.GitComposePath,
                GitCredentialId = stack.GitCredentialId,
            };
        }
        else if (templateId is > 0)
        {
            var template = await _templateService.GetAsync(templateId.Value, HttpContext.RequestAborted);
            if (template is null)
            {
                return RedirectToPage("/Apps/Index");
            }

            FromTemplateName = template.Name;
            Input.Name = StackCommands.Slugify(template.Name);
            Input.ComposeYaml = template.ComposeYaml;
        }
        else if (string.IsNullOrEmpty(Input.ComposeYaml))
        {
            Input.ComposeYaml = "services:\n  app:\n    image: nginx:alpine\n    restart: unless-stopped\n    ports:\n      - \"8080:80\"\n";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadListsAsync();
        if (!ValidateSource() || !ModelState.IsValid)
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
        await LoadListsAsync();
        if (!ValidateSource() || !ModelState.IsValid)
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
        // Tag helpers render posted ModelState values over model values; clear it so the (now saved)
        // hidden Id and readonly name reflect the new state instead of the original create-POST values.
        ModelState.Clear();
        DeployOutput = output;
        StatusMessage = ok ? "Stack saved & deployed." : "Saved, but deploy failed.";
        IsError = !ok;
        return Page();
    }

    /// <summary>Source-specific required fields (ComposeYaml for inline, GitRepoUrl for git).</summary>
    private bool ValidateSource()
    {
        if (Input.Source == "git")
        {
            if (string.IsNullOrWhiteSpace(Input.GitRepoUrl))
            {
                ModelState.AddModelError("Input.GitRepoUrl", "Git URL is required.");
            }
        }
        else if (string.IsNullOrWhiteSpace(Input.ComposeYaml))
        {
            ModelState.AddModelError("Input.ComposeYaml", "Compose YAML is required.");
        }

        return ModelState.IsValid;
    }

    private async Task<(bool Ok, string Message, long Id)> SaveAsync()
    {
        var isGit = Input.Source == "git";
        var input = new StackInput
        {
            Name = Input.Name,
            EnvironmentId = Input.EnvironmentId,
            ComposeYaml = isGit ? string.Empty : (Input.ComposeYaml ?? string.Empty),
            GitRepoUrl = isGit ? Input.GitRepoUrl : null,
            GitReference = isGit ? Input.GitReference : null,
            GitComposePath = isGit ? Input.GitComposePath : null,
            GitCredentialId = isGit ? Input.GitCredentialId : null,
        };

        if (IsEdit)
        {
            var (ok, message) = await _stackService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            return (ok, message, Input.Id!.Value);
        }

        return await _stackService.CreateAsync(input, HttpContext.RequestAborted);
    }

    private async Task LoadListsAsync()
    {
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        GitCredentials = await _gitCredentialService.GetAllAsync(HttpContext.RequestAborted);
    }
}
