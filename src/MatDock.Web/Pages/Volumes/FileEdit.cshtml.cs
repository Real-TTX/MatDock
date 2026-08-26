using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class FileEditModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly VolumeFileService _fileService;

    public FileEditModel(EnvironmentService environmentService, VolumeFileService fileService)
    {
        _environmentService = environmentService;
        _fileService = fileService;
    }

    [BindProperty(SupportsGet = true)] public long EnvId { get; set; }
    [BindProperty(SupportsGet = true)] public string Volume { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Path { get; set; } = string.Empty;

    [BindProperty] public string FileContent { get; set; } = string.Empty;

    public DockerEnvironment? Environment { get; private set; }
    public string FileName => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
    public string ParentPath => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : string.Empty;
    public bool IsBinary { get; private set; }
    public bool Truncated { get; private set; }
    public bool NonUtf8 { get; private set; }
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Environment = await _environmentService.GetAsync(EnvId, HttpContext.RequestAborted);
        if (Environment is not { IsEnabled: true })
        {
            return NotFound();
        }

        var (file, error) = await _fileService.ReadAsync(Environment, Volume, Path, HttpContext.RequestAborted);
        if (error is not null)
        {
            Error = error;
        }
        else if (file is not null)
        {
            IsBinary = file.IsBinary;
            Truncated = file.Truncated;
            NonUtf8 = file.NonUtf8;
            FileContent = file.IsBinary ? string.Empty : file.Content;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        Environment = await _environmentService.GetAsync(EnvId, HttpContext.RequestAborted);
        if (Environment is not { IsEnabled: true })
        {
            return NotFound();
        }

        // Server-side guard: re-read the current file and refuse to save anything that the editor may
        // only have shown partially/lossily (large, binary, or non-UTF-8) — never clobber it.
        var (current, error) = await _fileService.ReadAsync(Environment, Volume, Path, HttpContext.RequestAborted);
        if (error is not null)
        {
            Error = error;
            return Page();
        }

        if (current is null || !current.Editable)
        {
            IsBinary = current?.IsBinary ?? false;
            Truncated = current?.Truncated ?? false;
            NonUtf8 = current?.NonUtf8 ?? false;
            StatusMessage = "Datei kann nicht gespeichert werden (zu groß, binär oder keine UTF-8-Textdatei).";
            IsError = true;
            return Page();
        }

        var (ok, message) = await _fileService.WriteAsync(Environment, Volume, Path, FileContent, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        if (ok)
        {
            return RedirectToPage("/Volumes/Files", new { envId = EnvId, volume = Volume, path = ParentPath });
        }

        return Page();
    }
}
