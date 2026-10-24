using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Networks;

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
        public string Driver { get; set; } = "bridge";
        public bool Internal { get; set; }
        public string? Subnet { get; set; }
        public string? Gateway { get; set; }
        public string? IpRange { get; set; }
        public bool Ipv6 { get; set; }
        public string? Ipv6Subnet { get; set; }
        public string? Parent { get; set; }
    }

    /// <summary>Whether an address was entered (the section opens then).</summary>
    public bool HasAddresses => !string.IsNullOrWhiteSpace(Input.Subnet) || !string.IsNullOrWhiteSpace(Input.Parent) || Input.Ipv6;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

        var (ok, message) = await _connectionService.CreateNetworkAsync(
            _environmentService.BuildSettings(env), Input.Name.Trim(),
            string.IsNullOrWhiteSpace(Input.Driver) ? null : Input.Driver.Trim(), Input.Internal, HttpContext.RequestAborted,
            new MatDock.Core.Networks.NetworkIpam(Text(Input.Subnet), Text(Input.Gateway), Text(Input.IpRange), Input.Ipv6, Text(Input.Ipv6Subnet), Text(Input.Parent)));

        if (!ok)
        {
            // Shown on this form (the next page is this one again; a TempData message would never be rendered here).
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = false;
        return RedirectToPage("/Networks/Index", new { EnvId = env.Id, Refresh = true });
    }

    private async Task LoadAsync()
        => Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
}
