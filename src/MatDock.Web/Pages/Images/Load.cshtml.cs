using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Images;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Images;

/// <summary>
/// Loads images from a tar (as <c>docker save</c> writes it, plain or compressed) into the Docker of an environment: for hosts that cannot reach a registry.
/// An image tar is big, so this page has no size limit of its own; it streams the upload to the host.
/// </summary>
[DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue, ValueLengthLimit = int.MaxValue)]
public class LoadModel : PageModel
{
    private readonly EnvironmentService _environments;
    private readonly ImageService _images;

    public LoadModel(EnvironmentService environments, ImageService images)
    {
        _environments = environments;
        _images = images;
    }

    [BindProperty] public long EnvironmentId { get; set; }
    public List<DockerEnvironment> Environments { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        EnvironmentId = MatDock.Web.Support.EnvSelection.Current(HttpContext) ?? Environments.FirstOrDefault()?.Id ?? 0;
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? file)
    {
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        var env = Environments.FirstOrDefault(e => e.Id == EnvironmentId);
        if (env is null)
        {
            ModelState.AddModelError(string.Empty, "Please choose an environment.");
            return Page();
        }

        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Choose the file of an image (.tar or .tar.gz).");
            return Page();
        }

        await using var stream = file.OpenReadStream();
        var (ok, message) = await _images.LoadAsync(env, stream, HttpContext.RequestAborted);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = false;
        return Redirect($"/Images?EnvId={env.Id}");
    }
}
