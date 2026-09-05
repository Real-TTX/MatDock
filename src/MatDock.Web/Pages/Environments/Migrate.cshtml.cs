using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Environments;

public class MigrateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly VolumeMigrationService _migrationService;
    private readonly BackupTargetService _backupTargetService;
    private readonly ContainerService _containerService;

    public MigrateModel(
        EnvironmentService environmentService,
        IEnvironmentConnectionService connectionService,
        VolumeMigrationService migrationService,
        BackupTargetService backupTargetService,
        ContainerService containerService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _migrationService = migrationService;
        _backupTargetService = backupTargetService;
        _containerService = containerService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public DockerEnvironment? SourceEnvironment { get; private set; }

    /// <summary>All enabled environments — the source-environment picker.</summary>
    public List<DockerEnvironment> SourceEnvironments { get; private set; } = new();

    public List<DockerEnvironment> TargetEnvironments { get; private set; } = new();
    public List<BackupTarget> BackupTargets { get; private set; } = new();

    /// <summary>All volume names on the source environment (the multi-select list).</summary>
    public List<string> AvailableVolumes { get; private set; } = new();

    /// <summary>Set when the source volumes could not be listed (host unreachable).</summary>
    public string? LoadError { get; private set; }

    public IReadOnlyList<DockerContainer> AffectedContainers { get; private set; } = new List<DockerContainer>();
    public List<MigrationOutcome> Results { get; private set; } = new();

    public sealed record MigrationOutcome(string Label, bool Success, string Message, List<string> Steps);

    public class InputModel
    {
        public long SourceEnvId { get; set; }

        /// <summary>One or more source volumes to migrate (checkboxes).</summary>
        public List<string> SourceVolumes { get; set; } = new();

        [Range(1, long.MaxValue, ErrorMessage = "Please choose a target environment.")]
        public long TargetEnvId { get; set; }

        /// <summary>Optional rename — only applied when exactly one source volume is selected.</summary>
        public string? TargetVolume { get; set; }

        public bool Overwrite { get; set; }

        /// <summary>Direct streaming, or via a safety-net backup.</summary>
        public MigrationMode Mode { get; set; } = MigrationMode.Direct;

        /// <summary>0 = local MatDock storage; otherwise a configured backup target. Used only for ViaBackup.</summary>
        public long BackupTargetId { get; set; }

        /// <summary>Keep the intermediate backup after a successful ViaBackup migration.</summary>
        public bool KeepBackup { get; set; } = true;

        /// <summary>Stop the source volumes' containers during the transfer, then restart them.</summary>
        public bool StopContainers { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long sourceId, string? volume)
    {
        // The source can be chosen on the page; when none is given (e.g. the "Migrate" button on the
        // all-environments volume list) fall back to the first enabled environment.
        var enabled = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        if (sourceId <= 0)
        {
            sourceId = enabled.FirstOrDefault()?.Id ?? 0;
        }

        Input.SourceEnvId = sourceId;
        if (!string.IsNullOrWhiteSpace(volume))
        {
            Input.SourceVolumes = new List<string> { volume };
            Input.TargetVolume = volume;
        }
        Input.KeepBackup = true;

        await LoadAsync();
        Input.TargetEnvId = TargetEnvironments.FirstOrDefault()?.Id ?? 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (SourceEnvironment is null || !SourceEnvironment.IsEnabled)
        {
            return NotFound();
        }

        // Keep only real, valid selections (the checkboxes are drawn from the source's own list).
        var volumes = Input.SourceVolumes
            .Where(v => !string.IsNullOrWhiteSpace(v) && VolumeCommands.IsValidVolumeName(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (volumes.Count == 0)
        {
            ModelState.AddModelError("Input.SourceVolumes", "Please select at least one source volume.");
        }

        if (Input.TargetEnvId == Input.SourceEnvId)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Source and target must be different.");
        }

        // A rename only makes sense for a single volume; with several, each keeps its name.
        var rename = volumes.Count == 1 && !string.IsNullOrWhiteSpace(Input.TargetVolume)
            ? Input.TargetVolume!.Trim()
            : null;
        if (rename is not null && !VolumeCommands.IsValidVolumeName(rename))
        {
            ModelState.AddModelError("Input.TargetVolume", "Only letters, numbers and . _ - are allowed (must start alphanumeric).");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var target = await _environmentService.GetAsync(Input.TargetEnvId, HttpContext.RequestAborted);
        if (target is null || !target.IsEnabled)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Target environment not available (disabled or deleted).");
            return Page();
        }

        BackupTarget? backupTarget = null;
        if (Input.Mode == MigrationMode.ViaBackup && Input.BackupTargetId > 0)
        {
            backupTarget = await _backupTargetService.GetAsync(Input.BackupTargetId, HttpContext.RequestAborted);
            if (backupTarget is null)
            {
                ModelState.AddModelError("Input.BackupTargetId", "Backup target not found.");
                return Page();
            }
        }

        var sourceSettings = _environmentService.BuildSettings(SourceEnvironment);
        var targetSettings = _environmentService.BuildSettings(target);
        var results = new List<MigrationOutcome>();
        // Two selected volumes resolving to the same target name would silently overwrite each other.
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
        {
            var targetVolume = rename ?? volume;
            var label = $"{volume} → {target.Name}/{targetVolume}";

            if (!targetNames.Add(targetVolume))
            {
                results.Add(new MigrationOutcome(label, false,
                    $"Target volume name \"{targetVolume}\" appears more than once – skipped to avoid overwriting.",
                    new List<string>()));
                continue;
            }

            try
            {
                VolumeMigrationResult result;
                if (Input.Mode == MigrationMode.ViaBackup)
                {
                    result = await _migrationService.MigrateViaBackupAsync(
                        SourceEnvironment, volume, target, targetVolume,
                        Input.Overwrite, backupTarget, Input.KeepBackup, Input.StopContainers, HttpContext.RequestAborted);
                }
                else
                {
                    var request = new VolumeMigrationRequest
                    {
                        Source = sourceSettings,
                        SourceVolume = volume,
                        Target = targetSettings,
                        TargetVolume = targetVolume,
                        Overwrite = Input.Overwrite,
                        StopContainers = Input.StopContainers
                    };
                    result = await _migrationService.MigrateAsync(request, HttpContext.RequestAborted);
                }

                results.Add(new MigrationOutcome(label, result.Success, result.Message, result.Steps));
            }
            catch (Exception ex)
            {
                // One volume's failure must not discard the rest of the batch's report.
                results.Add(new MigrationOutcome(label, false, $"Error: {ex.Message}", new List<string>()));
            }
        }

        Results = results;
        return Page();
    }

    private async Task LoadAsync()
    {
        SourceEnvironment = await _environmentService.GetAsync(Input.SourceEnvId, HttpContext.RequestAborted);
        // Only active environments are valid migration sources/targets.
        var enabled = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        SourceEnvironments = enabled;
        TargetEnvironments = enabled.Where(e => e.Id != Input.SourceEnvId).ToList();
        BackupTargets = await _backupTargetService.GetAllAsync(HttpContext.RequestAborted);

        if (SourceEnvironment is { IsEnabled: true })
        {
            try
            {
                var volumes = await _connectionService.ListVolumesAsync(
                    _environmentService.BuildSettings(SourceEnvironment), HttpContext.RequestAborted);
                AvailableVolumes = volumes
                    .Select(v => v.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
            }

            // Show which containers on the source use the currently selected volumes.
            var affected = new List<DockerContainer>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var volume in Input.SourceVolumes.Where(VolumeCommands.IsValidVolumeName))
            {
                try
                {
                    foreach (var c in await _containerService.ListByVolumeAsync(SourceEnvironment, volume, HttpContext.RequestAborted))
                    {
                        if (seen.Add(c.Name))
                        {
                            affected.Add(c);
                        }
                    }
                }
                catch
                {
                    // Best-effort: the affected-container hint must not block the migration form.
                }
            }
            AffectedContainers = affected;
        }
    }
}
