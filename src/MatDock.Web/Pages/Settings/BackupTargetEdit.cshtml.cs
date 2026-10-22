using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
using MatDock.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

public class BackupTargetEditModel : PageModel
{
    private readonly BackupTargetService _targetService;

    public BackupTargetEditModel(BackupTargetService targetService)
    {
        _targetService = targetService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public string? TestMessage { get; private set; }
    public bool TestOk { get; private set; }

    public class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        public bool IsDefault { get; set; }

        /// <summary>Smb or Restic; fixed once the target exists (its backups were written that way).</summary>
        public BackupTargetType Type { get; set; } = BackupTargetType.Smb;

        // SMB — required only for Smb (checked by BackupTargetService.ValidateInput)
        [Display(Name = "Host")]
        public string? SmbHost { get; set; }

        [Display(Name = "Share")]
        public string? SmbShare { get; set; }

        [Display(Name = "Subfolder")]
        public string? SmbDirectory { get; set; }

        [Display(Name = "User")]
        public string? SmbUsername { get; set; }

        [Display(Name = "Domain")]
        public string? SmbDomain { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string? SmbPassword { get; set; }

        // restic
        [Display(Name = "Repository")]
        public string? ResticRepository { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Repository password")]
        public string? ResticPassword { get; set; }

        [Display(Name = "Access keys")]
        public string? ResticEnv { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var t = await _targetService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (t is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = t.Id,
                Name = t.Name,
                IsDefault = t.IsDefault,
                Type = t.Type == BackupTargetType.Restic ? BackupTargetType.Restic : BackupTargetType.Smb,
                SmbHost = t.SmbHost,
                SmbShare = t.SmbShare,
                SmbDirectory = t.SmbDirectory,
                SmbUsername = t.SmbUsername,
                SmbDomain = t.SmbDomain,
                // The repository is not a secret; the password and the keys are never sent back.
                ResticRepository = t.ResticRepository,
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await IsValidAsync())
        {
            return Page();
        }

        var input = ToInput();
        if (IsEdit)
        {
            var ok = await _targetService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!ok)
            {
                return NotFound();
            }
        }
        else
        {
            await _targetService.CreateAsync(input, HttpContext.RequestAborted);
        }

        return RedirectToPage("/Settings/Index", new { tab = "backup" });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var (ok, message) = await _targetService.DeleteAsync(id, HttpContext.RequestAborted);
        TempData["StatusMessage"] = message;
        TempData["IsError"] = !ok;
        return RedirectToPage("/Settings/Index", new { tab = "backup" });
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await IsValidAsync())
        {
            return Page();
        }

        var (ok, message) = await _targetService.TestInputAsync(ToInput(), Input.Id, HttpContext.RequestAborted);
        TestOk = ok;
        TestMessage = message;
        return Page();
    }

    /// <summary>The attributes first, then what depends on the type (host/share for SMB, repository/password/keys for restic).</summary>
    private async Task<bool> IsValidAsync()
    {
        if (Input.Type is not (BackupTargetType.Smb or BackupTargetType.Restic))
        {
            ModelState.AddModelError(string.Empty, "Choose a target type.");
        }

        if (ModelState.IsValid)
        {
            // An existing target keeps its type and, for restic, may leave the password blank.
            var stored = IsEdit ? await _targetService.GetAsync(Input.Id!.Value, HttpContext.RequestAborted) : null;
            if (stored is not null && stored.Type != Input.Type)
            {
                ModelState.AddModelError(string.Empty, "The type of a target cannot be changed; create a new target.");
            }
            else if (BackupTargetService.ValidateInput(ToInput(), stored?.EncryptedResticPassword is not null) is { } problem)
            {
                ModelState.AddModelError(string.Empty, problem);
            }
        }

        return ModelState.IsValid;
    }

    private BackupTargetInput ToInput() => new()
    {
        Name = Input.Name,
        Type = Input.Type,
        IsDefault = Input.IsDefault,
        SmbHost = Input.SmbHost,
        SmbShare = Input.SmbShare,
        SmbDirectory = Input.SmbDirectory,
        SmbUsername = Input.SmbUsername,
        SmbDomain = Input.SmbDomain,
        SmbPassword = Input.SmbPassword,
        ResticRepository = Input.ResticRepository,
        ResticPassword = Input.ResticPassword,
        ResticEnv = Input.ResticEnv,
    };
}
