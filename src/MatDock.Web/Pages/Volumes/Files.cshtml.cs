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
    private readonly IEnvironmentConnectionService _connectionService;

    public FilesModel(EnvironmentService environmentService, VolumeFileService fileService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _fileService = fileService;
        _connectionService = connectionService;
    }

    public long EnvId { get; private set; }
    public string Volume { get; private set; } = string.Empty;

    /// <summary>The container whose file system is shown (/Containers/Files); empty for a volume.</summary>
    public string Container { get; private set; } = string.Empty;

    public FileTarget Target => string.IsNullOrEmpty(Container) ? FileTarget.ForVolume(Volume) : FileTarget.ForContainer(Container);

    /// <summary>The page and the query part that name the target, for links.</summary>
    public string BasePath => Target.IsContainer ? "/Containers/Files" : "/Volumes/Files";
    public string TargetQuery => Target.IsContainer ? $"container={Uri.EscapeDataString(Container)}" : $"volume={Uri.EscapeDataString(Volume)}";
    public string Path { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }

    /// <summary>All volumes on this environment, for the volume switcher dropdown (best-effort).</summary>
    public List<string> VolumeNames { get; private set; } = new();
    public IReadOnlyList<VolumeFileEntry> Entries { get; private set; } = new List<VolumeFileEntry>();
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    /// <summary>Path segments for the breadcrumb: (name, cumulative rel path).</summary>
    public IReadOnlyList<(string Name, string Rel)> Crumbs { get; private set; } = new List<(string, string)>();

    public async Task<IActionResult> OnGetAsync(long envId, string? volume, string? container, string? path)
    {
        if (!await LoadEnvAsync(envId))
        {
            return NotFound();
        }

        EnvId = envId;
        Volume = volume ?? string.Empty;
        Container = container ?? string.Empty;
        Path = VolumeFileCommands.NormalizeRelPath(path) ?? string.Empty;
        BuildCrumbs();

        var (entries, error) = await _fileService.ListAsync(Environment!, Target, Path, HttpContext.RequestAborted);
        Entries = entries;
        Error = error;

        if (Target.IsContainer)
        {
            return Page();
        }

        // Populate the volume switcher (best-effort; on failure it just shows the current volume).
        try
        {
            var vols = await _connectionService.ListVolumesAsync(_environmentService.BuildSettings(Environment!), HttpContext.RequestAborted);
            VolumeNames = vols.Select(v => v.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { /* ignore */ }

        if (!VolumeNames.Contains(Volume))
        {
            VolumeNames.Insert(0, Volume);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostNewFileAsync(long envId, string? volume, string? container, string? path, string name)
    {
        var (ok, msg) = await Guard(envId, () => _fileService.CreateFileAsync(Environment!, T(volume, container), Combine(path, name), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, container, path);
    }

    public async Task<IActionResult> OnPostNewFolderAsync(long envId, string? volume, string? container, string? path, string name)
    {
        var (ok, msg) = await Guard(envId, () => _fileService.MakeDirAsync(Environment!, T(volume, container), Combine(path, name), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, container, path);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long envId, string? volume, string? container, string? path, string target)
    {
        var (ok, msg) = await Guard(envId, () => _fileService.DeleteAsync(Environment!, T(volume, container), target, HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, container, path);
    }

    public async Task<IActionResult> OnPostRenameAsync(long envId, string? volume, string? container, string? path, string target, string newName)
    {
        var (ok, msg) = await Guard(envId, () => _fileService.MoveAsync(Environment!, T(volume, container), target, Combine(path, newName), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, container, path);
    }

    public async Task<IActionResult> OnPostCopyAsync(long envId, string? volume, string? container, string? path, string target, string newName)
    {
        var (ok, msg) = await Guard(envId, () => _fileService.CopyAsync(Environment!, T(volume, container), target, Combine(path, newName), HttpContext.RequestAborted));
        return Finish(ok, msg, envId, volume, container, path);
    }

    public async Task<IActionResult> OnPostUploadAsync(long envId, string? volume, string? container, string? path, IFormFile? file)
    {
        if (!await LoadEnvAsync(envId))
        {
            return Finish(false, "Environment not available.", envId, volume, container, path);
        }

        if (file is null || file.Length == 0)
        {
            return Finish(false, "No file selected.", envId, volume, container, path);
        }

        // Strip any client-supplied directory components; place the file in the current folder.
        var dest = Combine(path, System.IO.Path.GetFileName(file.FileName));
        await using var stream = file.OpenReadStream();
        var (ok, msg) = await _fileService.UploadAsync(Environment!, T(volume, container), dest, stream, HttpContext.RequestAborted);
        return Finish(ok, msg, envId, volume, container, path);
    }

    public async Task<IActionResult> OnGetDownloadAsync(long envId, string? volume, string? container, string target)
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
        var (ok, _) = await _fileService.DownloadAsync(Environment!, T(volume, container), target, Response.Body, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    private async Task<(bool Ok, string Message)> Guard(long envId, Func<Task<(bool, string)>> action)
    {
        if (!await LoadEnvAsync(envId))
        {
            return (false, "Environment not available.");
        }

        return await action();
    }

    private IActionResult Finish(bool ok, string message, long envId, string? volume, string? container, string? path)
    {
        StatusMessage = message;
        IsError = !ok;
        return string.IsNullOrEmpty(container)
            ? Redirect($"/Volumes/Files?envId={envId}&volume={Uri.EscapeDataString(volume ?? string.Empty)}&path={Uri.EscapeDataString(path ?? string.Empty)}")
            : Redirect($"/Containers/Files?envId={envId}&container={Uri.EscapeDataString(container)}&path={Uri.EscapeDataString(path ?? string.Empty)}");
    }

    private static FileTarget T(string? volume, string? container)
        => string.IsNullOrEmpty(container) ? FileTarget.ForVolume(volume) : FileTarget.ForContainer(container);

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
