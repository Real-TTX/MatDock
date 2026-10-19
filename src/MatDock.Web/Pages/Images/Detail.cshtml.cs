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
    private readonly ImageScanService _scans;
    private readonly MatDock.Core.Operations.OperationRegistry _operations;

    public DetailModel(EnvironmentService environments, ImageService images, ImageScanService scans, MatDock.Core.Operations.OperationRegistry operations)
    {
        _environments = environments;
        _images = images;
        _scans = scans;
        _operations = operations;
    }

    /// <summary>The last vulnerability scan of this image here, or null.</summary>
    public ImageScan? Scan { get; private set; }

    /// <summary>A scan that runs now (started from this page).</summary>
    public MatDock.Core.Operations.Operation? Running { get; private set; }

    public long EnvId { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public ImageDetail? Detail { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(long envId, string? reference, string? op)
    {
        Running = string.IsNullOrEmpty(op) ? null : _operations.Get(op);
        EnvId = envId;
        Reference = reference ?? string.Empty;
        Environment = await _environments.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is null || !Environment.IsEnabled)
        {
            return NotFound();
        }

        (Detail, Error) = await _images.GetDetailAsync(Environment, Reference, HttpContext.RequestAborted);
        Scan = await _scans.GetAsync(envId, Reference, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>Starts a vulnerability scan in the background (it can take minutes); the page follows it.</summary>
    public async Task<IActionResult> OnPostScanAsync(long envId, string reference)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var env = await _environments.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled || !ImageCommands.IsValidImageRef(reference))
        {
            return NotFound();
        }

        var operation = _operations.Start("image-scan", reference, $"scan:{envId}:{reference}", async ctx =>
        {
            var scans = ctx.Services.GetRequiredService<ImageScanService>();
            var (ok, message, _) = await scans.ScanAsync(env, reference, ctx.CancellationToken, ctx.Log);
            ctx.Log(message);
            return ok ? MatDock.Core.Operations.OperationOutcome.Success() : MatDock.Core.Operations.OperationOutcome.Failure(message);
        });
        return Redirect($"/Images/Detail?envId={envId}&reference={Uri.EscapeDataString(reference)}&op={operation.Id}");
    }
}
