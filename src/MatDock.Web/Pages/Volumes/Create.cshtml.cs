using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class CreateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;

    public CreateModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<DockerEnvironment> Environments { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed class InputModel
    {
        public long EnvironmentId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>"simple" | "nfs" | "cifs" | "custom"</summary>
        public string Type { get; set; } = "simple";

        // NFS
        public string? NfsServer { get; set; }
        public string? NfsPath { get; set; }
        public string? NfsOptions { get; set; } = "rw,nfsvers=4";

        // CIFS / SMB
        public string? CifsShare { get; set; }
        public string? CifsUser { get; set; }
        public string? CifsPassword { get; set; }
        public string? CifsOptions { get; set; } = "vers=3.0,file_mode=0644,dir_mode=0755";

        // Custom
        public string? CustomDriver { get; set; } = "local";
        public string? CustomOptions { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Input.EnvironmentId = MatDock.Web.Support.EnvSelection.Current(HttpContext)
            ?? Environments.FirstOrDefault()?.Id ?? 0;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();

        var env = Environments.FirstOrDefault(e => e.Id == Input.EnvironmentId);
        if (env is null)
        {
            ModelState.AddModelError(string.Empty, "Please choose an environment.");
            return Page();
        }

        var (driver, options, error) = BuildSpec();
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        var (ok, message) = await _connectionService.CreateVolumeAsync(
            _environmentService.BuildSettings(env), Input.Name.Trim(), driver, options, HttpContext.RequestAborted);

        StatusMessage = message;
        IsError = !ok;
        if (ok)
        {
            return RedirectToPage("/Volumes/Index", new { EnvId = env.Id, Refresh = true });
        }

        return Page();
    }

    /// <summary>Turns the form into a docker (driver, --opt list) spec. Returns an error message if invalid.</summary>
    private (string? Driver, List<(string, string)> Options, string? Error) BuildSpec()
    {
        var opts = new List<(string, string)>();

        switch (Input.Type)
        {
            case "nfs":
                if (string.IsNullOrWhiteSpace(Input.NfsServer) || string.IsNullOrWhiteSpace(Input.NfsPath))
                {
                    return (null, opts, "NFS needs a server and a remote path.");
                }
                opts.Add(("type", "nfs"));
                var no = "addr=" + Input.NfsServer.Trim();
                if (!string.IsNullOrWhiteSpace(Input.NfsOptions)) { no += "," + Input.NfsOptions.Trim(); }
                opts.Add(("o", no));
                // NFS device is ":/remote/path" (address goes into o=addr=...).
                var path = Input.NfsPath.Trim();
                opts.Add(("device", path.StartsWith(':') ? path : ":" + path));
                return ("local", opts, null);

            case "cifs":
                if (string.IsNullOrWhiteSpace(Input.CifsShare))
                {
                    return (null, opts, "CIFS/SMB needs a share (e.g. //server/share).");
                }
                opts.Add(("type", "cifs"));
                opts.Add(("device", Input.CifsShare.Trim()));
                var co = string.Empty;
                if (!string.IsNullOrWhiteSpace(Input.CifsUser)) { co += "username=" + Input.CifsUser.Trim(); }
                if (!string.IsNullOrWhiteSpace(Input.CifsPassword)) { co += (co.Length > 0 ? "," : "") + "password=" + Input.CifsPassword; }
                if (!string.IsNullOrWhiteSpace(Input.CifsOptions)) { co += (co.Length > 0 ? "," : "") + Input.CifsOptions.Trim(); }
                if (co.Length > 0) { opts.Add(("o", co)); }
                return ("local", opts, null);

            case "custom":
                var driver = string.IsNullOrWhiteSpace(Input.CustomDriver) ? "local" : Input.CustomDriver.Trim();
                foreach (var raw in (Input.CustomOptions ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var eq = raw.IndexOf('=');
                    if (eq <= 0)
                    {
                        return (null, opts, $"Invalid option line (expected key=value): '{raw}'.");
                    }
                    opts.Add((raw[..eq].Trim(), raw[(eq + 1)..].Trim()));
                }
                return (driver, opts, null);

            default: // simple
                return (null, opts, null);
        }
    }

    private async Task LoadAsync()
        => Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
}
