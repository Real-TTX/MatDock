using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Git;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Templates;
using MatDock.Web.Api;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class EditModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;
    private readonly StackTemplateService _templateService;
    private readonly GitCredentialService _gitCredentialService;
    private readonly GitRepoService _gitRepoService;
    private readonly OperationRegistry _operations;

    public EditModel(
        StackService stackService,
        EnvironmentService environmentService,
        StackTemplateService templateService,
        GitCredentialService gitCredentialService,
        GitRepoService gitRepoService,
        OperationRegistry operations)
    {
        _operations = operations;
        _stackService = stackService;
        _environmentService = environmentService;
        _templateService = templateService;
        _gitCredentialService = gitCredentialService;
        _gitRepoService = gitRepoService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<GitCredential> GitCredentials { get; private set; } = new();
    public List<GitRepo> SavedRepos { get; private set; } = new();
    public List<StackTemplate> Apps { get; private set; } = new();
    public string? FromTemplateName { get; private set; }

    /// <summary>Owner of an externally managed stack (shown as a hint; a save here keeps it).</summary>
    public string? ManagedBy { get; private set; }

    /// <summary>The images MatDock runs instead of the ones in the compose file (shown as a hint; the file is not changed).</summary>
    public IReadOnlyDictionary<string, MatDock.Core.Stacks.ImageOverride> Overrides { get; private set; } = new Dictionary<string, MatDock.Core.Stacks.ImageOverride>();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    public string? DeployOutput { get; private set; }

    /// <summary>The deploy started from this page (<c>?op=</c>), shown in the console.</summary>
    public OperationConsoleModel? Console { get; private set; }

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

        /// <summary>Optional .env file for the inline stack (variable substitution + defaults).</summary>
        public string? EnvContent { get; set; }

        // Git source
        public string? GitRepoUrl { get; set; }
        public string? GitReference { get; set; }
        public string? GitComposePath { get; set; }
        public long? GitCredentialId { get; set; }

        // Deploy options: the stack's defaults, changed per deploy here and kept on save.
        public bool OptPull { get; set; }
        public bool OptBuild { get; set; }
        public bool OptPullBase { get; set; }
        public bool OptNoCache { get; set; }
        public bool OptForceRecreate { get; set; }
        public bool OptRemoveOrphans { get; set; }

        public DeployOptions Options => new()
        {
            Pull = OptPull, Build = OptBuild, PullBase = OptPullBase, NoCache = OptNoCache, ForceRecreate = OptForceRecreate, RemoveOrphans = OptRemoveOrphans,
        };

        public void SetOptions(DeployOptions o)
        {
            OptPull = o.Pull; OptBuild = o.Build; OptPullBase = o.PullBase; OptNoCache = o.NoCache; OptForceRecreate = o.ForceRecreate; OptRemoveOrphans = o.RemoveOrphans;
        }
    }

    public async Task<IActionResult> OnGetAsync(long? id, long? templateId, string? source, string? op)
    {
        await LoadListsAsync();

        if (id is > 0)
        {
            var stack = await _stackService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (stack is null)
            {
                return RedirectToPage("Index");
            }

            ManagedBy = stack.ManagedBy;
            Overrides = MatDock.Core.Stacks.ImageOverrides.Of(stack);
            Input = new InputModel
            {
                Id = stack.Id,
                Name = stack.Name,
                EnvironmentId = stack.EnvironmentId,
                Source = stack.IsGitBacked ? "git" : "inline",
                ComposeYaml = stack.ComposeYaml,
                EnvContent = stack.EnvContent,
                GitRepoUrl = stack.GitRepoUrl,
                GitReference = stack.GitReference,
                GitComposePath = stack.GitComposePath,
                GitCredentialId = stack.GitCredentialId,
            };
            Input.SetOptions(DeployOptions.FromJson(stack.DeployOptionsJson));

            if (!string.IsNullOrEmpty(op) && _operations.Get(op) is { } running)
            {
                Console = new OperationConsoleModel(running, running.IsFinished ? $"Deploy of {stack.Name}" : $"Deploying {stack.Name}…", $"deploy-{stack.Name}");
            }
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
        else if (string.Equals(source, "git", StringComparison.OrdinalIgnoreCase))
        {
            // Deep link from the Stacks page: start a new stack on the Git source tab.
            Input.Source = "git";
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

        // The deploy runs in the background; the page follows it in its console.
        var operation = StackOperations.Deploy(_operations, Input.EnvironmentId, result.Id, Input.Name.Trim(),
            new DeployRequest(Input.Options, DeployTrigger.Manual, User.Identity?.Name));
        // What the save said about an image override that the edit made obsolete stays visible.
        const string saved = "Stack saved. ";
        if (result.Message.StartsWith(saved, StringComparison.Ordinal))
        {
            StatusMessage = result.Message;
            IsError = false;
        }

        return RedirectToPage(new { id = result.Id, op = operation.Id });
    }

    /// <summary>Lets compose on the chosen environment check the compose file and the .env, without saving or deploying.</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        await LoadListsAsync();
        ModelState.Clear();
        if (Input.EnvironmentId <= 0 || string.IsNullOrWhiteSpace(Input.ComposeYaml))
        {
            StatusMessage = "Choose an environment and enter a compose file to check it.";
            IsError = true;
            return Page();
        }

        var (ok, output) = await _stackService.CheckComposeAsync(Input.EnvironmentId, Input.ComposeYaml, Input.EnvContent, HttpContext.RequestAborted);
        DeployOutput = output.Trim();
        StatusMessage = ok ? "Compose accepts the file." : "Compose found a problem.";
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
        else if (MatDock.Core.Stacks.ComposeCheck.Problem(Input.ComposeYaml) is { } problem)
        {
            ModelState.AddModelError("Input.ComposeYaml", problem);
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
            EnvContent = isGit ? null : Input.EnvContent,
            GitRepoUrl = isGit ? Input.GitRepoUrl : null,
            GitReference = isGit ? Input.GitReference : null,
            GitComposePath = isGit ? Input.GitComposePath : null,
            GitCredentialId = isGit ? Input.GitCredentialId : null,
        };

        (bool Ok, string Message, long Id) result;
        if (IsEdit)
        {
            var (ok, message) = await _stackService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            result = (ok, message, Input.Id!.Value);
        }
        else
        {
            result = await _stackService.CreateAsync(input, HttpContext.RequestAborted);
        }

        if (result.Ok)
        {
            await _stackService.SetDeployOptionsAsync(result.Id, Input.Options, HttpContext.RequestAborted);
        }

        return result;
    }

    /// <summary>Removes the stack from MatDock (as on the list); its containers keep running.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _stackService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Stack removed from MatDock." : "Stack not found.";
        IsError = !deleted;
        return RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        GitCredentials = await _gitCredentialService.GetAllAsync(HttpContext.RequestAborted);
        SavedRepos = await _gitRepoService.GetAllAsync(HttpContext.RequestAborted);
        Apps = await _templateService.GetAllAsync(HttpContext.RequestAborted);
        if (Input.Id is > 0)
        {
            ManagedBy = (await _stackService.GetAsync(Input.Id.Value, HttpContext.RequestAborted))?.ManagedBy;
        }
    }
}
