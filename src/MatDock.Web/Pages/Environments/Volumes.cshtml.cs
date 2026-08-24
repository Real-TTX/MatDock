using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Environments;

public class VolumesModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly ILogger<VolumesModel> _logger;

    public VolumesModel(EnvironmentService environmentService, ILogger<VolumesModel> logger)
    {
        _environmentService = environmentService;
        _logger = logger;
    }

    public DockerEnvironment? Environment { get; private set; }
    public IReadOnlyList<DockerVolume> Volumes { get; private set; } = new List<DockerVolume>();
    public bool Loaded { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Environment = await _environmentService.GetAsync(id, HttpContext.RequestAborted);
        if (Environment is null)
        {
            return NotFound();
        }

        try
        {
            Volumes = await _environmentService.ListVolumesAsync(Environment, HttpContext.RequestAborted);
            Loaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to list volumes for environment {Id}.", id);
            ErrorMessage = $"Volumes konnten nicht geladen werden: {ex.Message}";
        }

        return Page();
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return "–";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
