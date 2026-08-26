using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class BulkMigrateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly VolumeMigrationService _migrationService;

    public BulkMigrateModel(EnvironmentService environmentService, VolumeMigrationService migrationService)
    {
        _environmentService = environmentService;
        _migrationService = migrationService;
    }

    [BindProperty]
    public List<string> Items { get; set; } = new();

    [BindProperty]
    public long TargetEnvId { get; set; }

    [BindProperty]
    public bool Overwrite { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<(long EnvId, string EnvName, string Volume)> Selection { get; private set; } = new();
    public List<(string Label, bool Ok, string Message)>? Results { get; private set; }

    public async Task<IActionResult> OnPostAsync(string[] selected)
    {
        Items = (selected ?? Array.Empty<string>()).ToList();
        await LoadAsync();
        if (Selection.Count == 0)
        {
            return RedirectToPage("/Volumes/Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        await LoadAsync();
        if (Selection.Count == 0)
        {
            return RedirectToPage("/Volumes/Index");
        }

        if (TargetEnvId <= 0)
        {
            ModelState.AddModelError(nameof(TargetEnvId), "Bitte ein Ziel-Environment wählen.");
            return Page();
        }

        var targetEnv = await _environmentService.GetAsync(TargetEnvId, HttpContext.RequestAborted);
        if (targetEnv is null)
        {
            ModelState.AddModelError(nameof(TargetEnvId), "Ziel-Environment nicht gefunden.");
            return Page();
        }

        var targetSettings = _environmentService.BuildSettings(targetEnv);
        var results = new List<(string, bool, string)>();
        var envCache = new Dictionary<long, DockerEnvironment?>();

        foreach (var (envId, envName, volume) in Selection)
        {
            if (envId == TargetEnvId)
            {
                results.Add(($"{envName}/{volume}", false, "Quelle und Ziel identisch – übersprungen."));
                continue;
            }

            if (!envCache.TryGetValue(envId, out var srcEnv))
            {
                srcEnv = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
                envCache[envId] = srcEnv;
            }
            if (srcEnv is null)
            {
                results.Add(($"{envName}/{volume}", false, "Quell-Environment nicht gefunden."));
                continue;
            }

            var request = new VolumeMigrationRequest
            {
                Source = _environmentService.BuildSettings(srcEnv),
                SourceVolume = volume,
                Target = targetSettings,
                TargetVolume = volume,
                Overwrite = Overwrite
            };

            var result = await _migrationService.MigrateAsync(request, HttpContext.RequestAborted);
            results.Add(($"{envName}/{volume} → {targetEnv.Name}", result.Success, result.Message));
        }

        Results = results;
        return Page();
    }

    private async Task LoadAsync()
    {
        Environments = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        var names = Environments.ToDictionary(e => e.Id, e => e.Name);
        Selection = VolumeSelection.Parse(Items)
            .Select(s => (s.EnvId, names.TryGetValue(s.EnvId, out var n) ? n : $"#{s.EnvId}", s.Volume))
            .ToList();
    }
}
