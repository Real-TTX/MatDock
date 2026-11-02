using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

/// <summary>
/// Takes compose projects that live in a folder of a host over as stacks: scan the folder, choose the files (a project that runs already is not chosen
/// beforehand, one MatDock manages cannot be chosen), import. The files stay where they are; the stacks point their relative paths there.
/// </summary>
public class ImportModel : PageModel
{
    private readonly StackService _stacks;
    private readonly EnvironmentService _environments;

    public ImportModel(StackService stacks, EnvironmentService environments)
    {
        _stacks = stacks;
        _environments = environments;
    }

    public sealed class Row
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Selected { get; set; }
        public bool Running { get; set; }
        public bool Managed { get; set; }
    }

    [BindProperty] public long EnvironmentId { get; set; }
    [BindProperty] public string? Folder { get; set; }
    [BindProperty] public List<Row> Rows { get; set; } = [];

    public List<DockerEnvironment> Environments { get; private set; } = [];
    public bool Scanned { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<(string Path, bool Ok, string Message)> Results { get; private set; } = [];

    public async Task OnGetAsync(long? envId)
    {
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        EnvironmentId = envId ?? EnvSelection.Resolve(HttpContext) ?? Environments.FirstOrDefault()?.Id ?? 0;
    }

    public async Task<IActionResult> OnPostScanAsync()
    {
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        ModelState.Clear();
        Rows = [];
        var env = Environments.FirstOrDefault(e => e.Id == EnvironmentId);
        if (env is null)
        {
            Error = "Choose an environment.";
            return Page();
        }

        var (found, error) = await _stacks.ScanFolderAsync(env, Folder ?? string.Empty, HttpContext.RequestAborted);
        Error = error;
        Scanned = error is null;
        Rows = found.Select(f => new Row { Path = f.Path, Name = f.Name, Running = f.Running, Managed = f.Managed, Selected = !f.Running && !f.Managed }).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        var env = Environments.FirstOrDefault(e => e.Id == EnvironmentId);
        var chosen = Rows.Where(r => r.Selected && !r.Managed && !string.IsNullOrWhiteSpace(r.Path)).Select(r => (r.Path, r.Name?.Trim() ?? string.Empty)).ToList();
        if (env is null || chosen.Count == 0)
        {
            Error = env is null ? "Choose an environment." : "Choose at least one compose file.";
            Scanned = Rows.Count > 0;
            return Page();
        }

        Results = await _stacks.ImportAsync(env, chosen, HttpContext.RequestAborted);
        Scanned = false;
        Rows = [];
        return Page();
    }
}
