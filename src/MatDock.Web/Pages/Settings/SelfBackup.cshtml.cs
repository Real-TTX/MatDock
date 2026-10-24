using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Schedules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

/// <summary>
/// The backup of MatDock itself: a download now (database and key ring, encrypted with a passphrase), a restore from such a file (it takes effect at the
/// next start), and the schedules that write one regularly.
/// </summary>
[DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
public class SelfBackupModel : PageModel
{
    private readonly SelfBackupService _backups;
    private readonly ScheduleService _schedules;
    private readonly IHostApplicationLifetime _lifetime;

    public SelfBackupModel(SelfBackupService backups, ScheduleService schedules, IHostApplicationLifetime lifetime)
    {
        _backups = backups;
        _schedules = schedules;
        _lifetime = lifetime;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public bool HasPendingRestore { get; private set; }
    public IReadOnlyList<ScheduledTask> Schedules { get; private set; } = Array.Empty<ScheduledTask>();

    /// <summary>In a container (the .NET images say so) a restart policy brings MatDock back after it stopped, so it may stop itself.</summary>
    public static bool InContainer => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        HasPendingRestore = _backups.HasPendingRestore;
        Schedules = (await _schedules.GetAllAsync(HttpContext.RequestAborted)).Where(s => s.Action == ScheduleAction.SelfBackup).ToList();
    }

    /// <summary>The backup as a download.</summary>
    public async Task<IActionResult> OnPostDownloadAsync(string? passphrase, string? repeat)
    {
        var problem = SelfBackupService.PassphraseProblem(passphrase)
            ?? (passphrase != repeat ? "The two passphrases differ." : null);
        if (problem is not null)
        {
            StatusMessage = problem;
            IsError = true;
            return RedirectToPage();
        }

        var data = await _backups.CreateAsync(passphrase!, HttpContext.RequestAborted);
        return File(data, "application/octet-stream", SelfBackupService.FileName(DateTime.UtcNow));
    }

    /// <summary>Checks an uploaded backup and puts it aside for the next start.</summary>
    public async Task<IActionResult> OnPostRestoreAsync(IFormFile? file, string? passphrase)
    {
        if (file is null || file.Length == 0 || string.IsNullOrEmpty(passphrase))
        {
            StatusMessage = "Choose the backup file and enter its passphrase.";
            IsError = true;
            return RedirectToPage();
        }

        await using var stream = file.OpenReadStream();
        var (ok, message) = await _backups.PrepareRestoreAsync(stream, passphrase, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage();
    }

    public IActionResult OnPostCancel()
    {
        _backups.CancelPendingRestore();
        StatusMessage = "The prepared restore was dropped.";
        return RedirectToPage();
    }

    /// <summary>Stops MatDock so that its container restarts it and the prepared restore takes effect (only in a container).</summary>
    public IActionResult OnPostRestart()
    {
        if (!InContainer || !_backups.HasPendingRestore)
        {
            return RedirectToPage();
        }

        // A non-zero exit code, so a restart policy "on-failure" brings it back as well as "unless-stopped". The answer goes out first.
        Environment.ExitCode = 75;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            _lifetime.StopApplication();
        });
        StatusMessage = "MatDock restarts with the restored data. Sign in again in a few seconds.";
        return RedirectToPage();
    }
}
