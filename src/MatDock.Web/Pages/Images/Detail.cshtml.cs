using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Images;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Images;

public class DetailModel : PageModel
{
    private readonly EnvironmentService _environments;
    private readonly ImageService _images;

    public DetailModel(EnvironmentService environments, ImageService images)
    {
        _environments = environments;
        _images = images;
    }

    public long EnvId { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public ImageDetail? Detail { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(long envId, string? reference)
    {
        EnvId = envId;
        Reference = reference ?? string.Empty;
        Environment = await _environments.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is null || !Environment.IsEnabled)
        {
            return NotFound();
        }

        (Detail, Error) = await _images.GetDetailAsync(Environment, Reference, HttpContext.RequestAborted);
        return Page();
    }
}
