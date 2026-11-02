using MatDock.Core.Containers;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

/// <summary>
/// A compose file for a running container (<see cref="ComposeFromContainer"/>): to copy, to download, or to keep as a new stack (not deployed). Variables
/// with secret names are <c>${NAME}</c> in the file; a new stack gets their values as its secrets, the page never shows them.
/// </summary>
public class FromContainerModel : PageModel
{
    private readonly ContainerService _containers;
    private readonly EnvironmentService _environments;
    private readonly StackService _stacks;

    public FromContainerModel(ContainerService containers, EnvironmentService environments, StackService stacks)
    {
        _containers = containers;
        _environments = environments;
        _stacks = stacks;
    }

    [BindProperty(SupportsGet = true)] public long EnvId { get; set; }
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = string.Empty;
    [BindProperty] public string? Name { get; set; }

    public string? ContainerName { get; private set; }
    public string? Yaml { get; private set; }
    public IReadOnlyList<string> SecretNames { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var result = await RenderAsync();
        if (result is null)
        {
            return Page();
        }

        Name ??= StackCommands.Slugify(ContainerName);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await RenderAsync();
        if (result is null)
        {
            return Page();
        }

        var name = Name?.Trim() ?? string.Empty;
        var (ok, message, stackId) = await _stacks.CreateAsync(new StackInput { Name = name, EnvironmentId = EnvId, ComposeYaml = result.Yaml }, HttpContext.RequestAborted);
        if (!ok)
        {
            Error = message;
            return Page();
        }

        if (result.Secrets.Count > 0)
        {
            await _stacks.SetSecretsAsync(stackId, result.Secrets, [], HttpContext.RequestAborted);
        }

        TempData["StatusMessage"] = $"Stack '{name}' created from the container (not deployed). Stop or remove the container before you deploy it: name and ports would clash.";
        TempData["IsError"] = false;
        return Redirect($"/Stacks/Edit?id={stackId}");
    }

    private async Task<ComposeFromContainer.Result?> RenderAsync()
    {
        var env = await _environments.GetAsync(EnvId, HttpContext.RequestAborted);
        if (env is not { IsEnabled: true })
        {
            Error = "Environment not available.";
            return null;
        }

        if (await _containers.GetRunSpecAsync(env, Id, HttpContext.RequestAborted) is not { } found)
        {
            Error = "The container was not found.";
            return null;
        }

        ContainerName = found.Container.Name.TrimStart('/');
        var service = found.Container.Service is { } s && StackCommands.IsValidServiceName(s) ? s : StackCommands.Slugify(ContainerName);
        var result = ComposeFromContainer.Render(found.Spec, service);
        Yaml = result.Yaml;
        SecretNames = result.Secrets.Keys.ToList();
        return result;
    }
}
