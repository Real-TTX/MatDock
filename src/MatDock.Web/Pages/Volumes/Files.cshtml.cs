using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class FilesModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly VolumeFileService _fileService;

    public FilesModel(EnvironmentService environmentService, VolumeFileService fileService)
    {
        _environmentService = environmentService;
        _fileService = fileService;
    }

    public long EnvId { get; private set; }
    public string Volume { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public IReadOnlyList<VolumeFileEntry> Entries { get; private set; } = new List<VolumeFileEntry>();
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    /// <summary>Path segments for the breadcrumb: (name, cumulative rel path).</summary>
    public IReadOnlyList<(string Name, string Rel)> Crumbs { get; private set; } = new List<(string, string)>();

    public async Task<IActionResult> OnGetAsync(long envId, string volume, string? path)
    {
        if (!await LoadEnvAsync(envId))
        {
            return NotFound();
        }

        EnvId = envId;
        Volume = volume;
        Path = VolumeFileCommands.NormalizeRelPath(path) ?? string.Empty;
        BuildCrumbs();

        var (entries, error) = await _fileService.ListAsync(Environment!, Volume, Path, HttpContext.RequestAborted);
        Entries = entries;
        Error = error;
        return Page();
    }

    public async Task<IActionResult> OnPostNewFileAsync(long envId, string volume, string? path, string name)
    {
        var (ok, msg) = await Guard(envId, volume, () => _fileService.CreateFileAsync(Environment!, volume, Combine(path, name), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, path);
    }

    public async Task<IActionResult> OnPostNewFolderAsync(long envId, string volume, string? path, string name)
    {
        var (ok, msg) = await Guard(envId, volume, () => _fileService.MakeDirAsync(Environment!, volume, Combine(path, name), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, path);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long envId, string volume, string? path, string target)
    {
        var (ok, msg) = await Guard(envId, volume, () => _fileService.DeleteAsync(Environment!, volume, target, HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, path);
    }

    public async Task<IActionResult> OnPostRenameAsync(long envId, string volume, string? path, string target, string newName)
    {
        var (ok, msg) = await Guard(envId, volume, () => _fileService.MoveAsync(Environment!, volume, target, Combine(path, newName), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, path);
    }

    public async Task<IActionResult> OnPostCopyAsync(long envId, string volume, string? path, string target, string newName)
    {
        var (ok, msg) = await Guard(envId, volume, () => _fileService.CopyAsync(Environment!, volume, target, Combine(path, newName), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, path);
    }

    public async Task<IActionResult> OnPostUploadAsync(long envId, string volume, string? path, IFormFile? file)
    {
        if (!await LoadEnvAsync(envId))
        {
            return Finish(false, "Environment not available.", envId, volume, path);
        }

        if (file is null || file.Length == 0)
        {
            return Finish(false, "No file selected.", envId, volume, path);
        }

        // Strip any client-supplied directory components; place the file in the current folder.
        var dest = Combine(path, System.IO.Path.GetFileName(file.FileName));
        await using var stream = file.OpenReadStream();
        var (ok, msg) = await _fileService.UploadAsync(Environment!, volume, dest, stream, HttpContext.RequestAborted);
        return Finish(ok, msg, envId, volume, path);
    }

    public async Task<IActionResult> OnGetDownloadAsync(long envId, string volume, string target)
    {
        if (!await LoadEnvAsync(envId))
        {
            return NotFound();
        }

        var name = target.Split('/').LastOrDefault();
        // Sanitize for the Content-Disposition header (no control chars or quotes).
        var safe = new string((name ?? string.Empty).Where(ch => ch >= ' ' && ch != '"').ToArray());
        if (safe.Length == 0)
        {
            safe = "download";
        }

        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{safe}\"";
        var (ok, _) = await _fileService.DownloadAsync(Environment!, volume, target, Response.Body, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    private async Task<(bool Ok, string Message)> Guard(long envId, string volume, Func<Task<(bool, string)>> action)
    {
        if (!await LoadEnvAsync(envId))
        {
            return (false, "Environment not available.");
        }

        return await action();
    }

    private IActionResult Finish(bool ok, string message, long envId, string volume, string? path)
    {
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { envId, volume, path });
    }

    private async Task<bool> LoadEnvAsync(long envId)
    {
        Environment = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        return Environment is { IsEnabled: true };
    }

    /// <summary>Joins the current directory with a user-entered name, then normalizes/validates.</summary>
    private static string Combine(string? dir, string? name)
    {
        var d = VolumeFileCommands.NormalizeRelPath(dir) ?? string.Empty;
        var joined = string.IsNullOrEmpty(d) ? (name ?? string.Empty) : d + "/" + (name ?? string.Empty);
        return joined;
    }

    private void BuildCrumbs()
    {
        var crumbs = new List<(string, string)> { ("/", string.Empty) };
        if (Path.Length > 0)
        {
            var acc = "";
            foreach (var seg in Path.Split('/'))
            {
                acc = acc.Length == 0 ? seg : acc + "/" + seg;
                crumbs.Add((seg, acc));
            }
        }

        Crumbs = crumbs;
    }
}
