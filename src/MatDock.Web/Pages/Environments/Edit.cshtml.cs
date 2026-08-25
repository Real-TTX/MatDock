using System.ComponentModel.DataAnnotations;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Environments;

public class EditModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;

    public EditModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;

    public DockerConnectionResult? TestResult { get; private set; }

    /// <summary>Set after "Schlüsselpaar erzeugen"; shown so the user can install it on the host.</summary>
    public string? GeneratedPublicKey { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Bitte einen Namen angeben.")]
        [StringLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        [Display(Name = "Beschreibung")]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Bitte den Host angeben.")]
        [StringLength(255)]
        [Display(Name = "Host")]
        public string Host { get; set; } = string.Empty;

        [Range(1, 65535, ErrorMessage = "Port muss zwischen 1 und 65535 liegen.")]
        [Display(Name = "Port")]
        public int Port { get; set; } = 22;

        [Required(ErrorMessage = "Bitte den SSH-Benutzer angeben.")]
        [StringLength(128)]
        [Display(Name = "SSH-Benutzer")]
        public string Username { get; set; } = string.Empty;

        [Display(Name = "Authentifizierung")]
        public AuthType AuthType { get; set; } = AuthType.Password;

        [DataType(DataType.Password)]
        [Display(Name = "Passwort")]
        public string? Password { get; set; }

        [Display(Name = "Privater SSH-Schlüssel (PEM)")]
        public string? PrivateKeyPem { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Schlüssel-Passphrase")]
        public string? PrivateKeyPassphrase { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var entity = await _environmentService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (entity is null)
            {
                return NotFound();
            }

            MapToInput(entity);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        ValidateSecretsForSave();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = ToServiceInput();
        if (IsEdit)
        {
            var updated = await _environmentService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!updated)
            {
                return NotFound();
            }

            StatusMessage = "Environment gespeichert.";
        }
        else
        {
            var created = await _environmentService.CreateAsync(input, HttpContext.RequestAborted);
            Input.Id = created.Id;
            StatusMessage = "Environment angelegt.";
        }

        IsError = false;
        return RedirectToPage("/Environments/Index");
    }

    public IActionResult OnPostGenerateKey()
    {
        var comment = string.IsNullOrWhiteSpace(Input.Host) ? "matdock" : $"matdock@{Input.Host.Trim()}";
        var keyPair = SshKeygen.Generate(comment);

        Input.AuthType = AuthType.PrivateKey;
        Input.PrivateKeyPem = keyPair.PrivateKeyPem;
        Input.Password = null;
        GeneratedPublicKey = keyPair.PublicKeyOpenSsh;

        // Re-render the form with the freshly generated values (drop the posted-back ModelState).
        ModelState.Clear();
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        // Only the connection fields matter for a test; ignore Name/Description validation.
        ModelState.Remove("Input.Name");

        if (string.IsNullOrWhiteSpace(Input.Host) || string.IsNullOrWhiteSpace(Input.Username))
        {
            ModelState.AddModelError(string.Empty, "Host und Benutzer werden für den Test benötigt.");
            return Page();
        }

        try
        {
            var settings = await _environmentService.BuildTestSettingsAsync(ToServiceInput(), Input.Id, HttpContext.RequestAborted);
            TestResult = await _connectionService.TestConnectionAsync(settings, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            TestResult = DockerConnectionResult.Fail($"Zugangsdaten konnten nicht gelesen werden: {ex.Message}");
        }

        return Page();
    }

    private EnvironmentInput ToServiceInput() => new()
    {
        Name = Input.Name,
        Description = Input.Description,
        Host = Input.Host,
        Port = Input.Port,
        Username = Input.Username,
        AuthType = Input.AuthType,
        Password = Input.Password,
        PrivateKeyPem = Input.PrivateKeyPem,
        PrivateKeyPassphrase = Input.PrivateKeyPassphrase
    };

    private void MapToInput(DockerEnvironment entity)
    {
        Input = new InputModel
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Host = entity.Host,
            Port = entity.Port,
            Username = entity.Username,
            AuthType = entity.AuthType
            // Secrets are intentionally never sent back to the browser.
        };
    }

    private void ValidateSecretsForSave()
    {
        if (IsEdit)
        {
            return; // blank secret = keep existing
        }

        if (Input.AuthType == AuthType.Password && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Bitte ein Passwort angeben.");
        }
        else if (Input.AuthType == AuthType.PrivateKey && string.IsNullOrWhiteSpace(Input.PrivateKeyPem))
        {
            ModelState.AddModelError("Input.PrivateKeyPem", "Bitte den privaten Schlüssel angeben.");
        }
    }
}
