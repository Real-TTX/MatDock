using System.Globalization;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

/// <summary>
/// Starts a one-off container (<c>docker run -d</c>) from a form. Not managed: use a stack for anything that should stay. With <c>?from=</c> the form is filled
/// with the settings of a container that exists (duplicate); the values of its secret-looking variables stay masked here and are taken from it on the server.
/// </summary>
public class CreateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly ContainerService _containerService;

    public CreateModel(EnvironmentService environmentService, ContainerService containerService)
    {
        _environmentService = environmentService;
        _containerService = containerService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<DockerEnvironment> Environments { get; private set; } = new();

    /// <summary>The name of the container this form was filled from (a duplicate), or null.</summary>
    public string? FromName { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed class InputModel
    {
        public long EnvironmentId { get; set; }
        public string Image { get; set; } = string.Empty;
        public string? Name { get; set; }

        /// <summary>The container a duplicate was made from: its masked values are taken from it.</summary>
        public string? FromId { get; set; }

        // One entry per line in each of these.
        public string? Ports { get; set; }
        public string? Env { get; set; }
        public string? Volumes { get; set; }
        public string? Labels { get; set; }
        public string? Command { get; set; }
        public string? NetworkAliases { get; set; }
        public string? CapAdd { get; set; }
        public string? CapDrop { get; set; }
        public string? Devices { get; set; }
        public string? Dns { get; set; }
        public string? ExtraHosts { get; set; }

        public string Restart { get; set; } = "unless-stopped";
        public string? Network { get; set; }
        public string? Ip { get; set; }
        public string? Entrypoint { get; set; }
        public string? Memory { get; set; }
        public string? Cpus { get; set; }
        public string? Hostname { get; set; }
        public string? User { get; set; }
        public string? WorkDir { get; set; }
        public bool Privileged { get; set; }
        public string? Gpus { get; set; }
        public string? LogDriver { get; set; }
        public string? LogMaxSize { get; set; }
        public int? LogMaxFile { get; set; }
        public string? HealthCmd { get; set; }
        public string? HealthInterval { get; set; }
        public int? HealthRetries { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string? from = null, long? envId = null)
    {
        await LoadAsync();
        Input.EnvironmentId = envId ?? MatDock.Web.Support.EnvSelection.Current(HttpContext) ?? Environments.FirstOrDefault()?.Id ?? 0;
        if (string.IsNullOrEmpty(from))
        {
            return Page();
        }

        var env = Environments.FirstOrDefault(e => e.Id == Input.EnvironmentId);
        if (env is null || await _containerService.GetRunSpecAsync(env, from, HttpContext.RequestAborted) is not { } found)
        {
            return NotFound();
        }

        FromName = found.Container.Name;
        Input = FromSpec(found.Spec, env.Id, found.Container.Id);
        Input.Name = $"{found.Container.Name}-copy";
        return Page();
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

        var spec = ToSpec(Input);

        // A duplicate shows its secrets masked: take the real values from the container it was made from.
        if (!string.IsNullOrEmpty(Input.FromId) && spec.Env.Any(e => e.EndsWith("=" + ContainerInspect.Mask, StringComparison.Ordinal)))
        {
            var source = await _containerService.GetRunSpecAsync(env, Input.FromId, HttpContext.RequestAborted);
            var real = source?.Container.Env.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>();
            spec.Env = spec.Env
                .Select(e =>
                {
                    var eq = e.IndexOf('=');
                    return eq > 0 && e[(eq + 1)..] == ContainerInspect.Mask && real.TryGetValue(e[..eq], out var value) ? $"{e[..eq]}={value}" : e;
                })
                .ToList();
        }

        if (spec.Env.Any(e => e.EndsWith("=" + ContainerInspect.Mask, StringComparison.Ordinal)))
        {
            ModelState.AddModelError(string.Empty, "A variable still has the masked value " + ContainerInspect.Mask + ": enter its value.");
            return Page();
        }

        var invalid = ContainerRunCommands.Validate(spec);
        if (invalid is not null)
        {
            ModelState.AddModelError(string.Empty, invalid);
            return Page();
        }

        var (ok, message) = await _containerService.RunAsync(env, spec, HttpContext.RequestAborted);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = false;
        return RedirectToPage("/Containers/Index");
    }

    public static ContainerRunSpec ToSpec(InputModel input) => new()
    {
        Image = (input.Image ?? string.Empty).Trim(),
        Name = Text(input.Name),
        Ports = Lines(input.Ports),
        Env = Lines(input.Env),
        Volumes = Lines(input.Volumes),
        Labels = Lines(input.Labels),
        Command = Lines(input.Command),
        Restart = string.IsNullOrWhiteSpace(input.Restart) ? "no" : input.Restart.Trim(),
        Network = Text(input.Network),
        Ip = Text(input.Ip),
        NetworkAliases = Lines(input.NetworkAliases),
        Entrypoint = Text(input.Entrypoint),
        Memory = Text(input.Memory),
        Cpus = Text(input.Cpus)?.Replace(',', '.'),
        Hostname = Text(input.Hostname),
        User = Text(input.User),
        WorkDir = Text(input.WorkDir),
        Privileged = input.Privileged,
        CapAdd = Lines(input.CapAdd).Select(c => c.ToUpperInvariant()).ToList(),
        CapDrop = Lines(input.CapDrop).Select(c => c.ToUpperInvariant()).ToList(),
        Devices = Lines(input.Devices),
        Gpus = Text(input.Gpus),
        LogDriver = Text(input.LogDriver),
        LogMaxSize = Text(input.LogMaxSize),
        LogMaxFile = input.LogMaxFile,
        Dns = Lines(input.Dns),
        ExtraHosts = Lines(input.ExtraHosts),
        HealthCmd = Text(input.HealthCmd),
        HealthInterval = Text(input.HealthInterval),
        HealthRetries = input.HealthRetries,
    };

    /// <summary>The form filled with the settings of a container; secret-looking values masked.</summary>
    public static InputModel FromSpec(ContainerRunSpec spec, long environmentId, string? fromId) => new()
    {
        EnvironmentId = environmentId,
        FromId = fromId,
        Image = spec.Image,
        Name = spec.Name,
        Ports = Join(spec.Ports),
        Env = Join(spec.Env.Select(e =>
        {
            var eq = e.IndexOf('=');
            return eq > 0 && ContainerInspect.IsSecretName(e[..eq]) && eq < e.Length - 1 ? e[..(eq + 1)] + ContainerInspect.Mask : e;
        })),
        Volumes = Join(spec.Volumes),
        Labels = Join(spec.Labels),
        Command = Join(spec.Command),
        Restart = spec.Restart,
        Network = spec.Network,
        Ip = spec.Ip,
        NetworkAliases = Join(spec.NetworkAliases),
        Entrypoint = spec.Entrypoint,
        Memory = spec.Memory,
        Cpus = spec.Cpus,
        Hostname = spec.Hostname,
        User = spec.User,
        WorkDir = spec.WorkDir,
        Privileged = spec.Privileged,
        CapAdd = Join(spec.CapAdd),
        CapDrop = Join(spec.CapDrop),
        Devices = Join(spec.Devices),
        Gpus = spec.Gpus,
        LogDriver = spec.LogDriver,
        LogMaxSize = spec.LogMaxSize,
        LogMaxFile = spec.LogMaxFile,
        Dns = Join(spec.Dns),
        ExtraHosts = Join(spec.ExtraHosts),
        HealthCmd = spec.HealthCmd,
        HealthInterval = spec.HealthInterval,
        HealthRetries = spec.HealthRetries,
    };

    /// <summary>Whether a section of the form has something in it (it opens then).</summary>
    public bool Has(params string?[] values) => values.Any(v => !string.IsNullOrWhiteSpace(v));

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(IEnumerable<string> lines) => string.Join('\n', lines) is { Length: > 0 } text ? text : null;

    private static List<string> Lines(string? text)
        => (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async Task LoadAsync()
        => Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
}
