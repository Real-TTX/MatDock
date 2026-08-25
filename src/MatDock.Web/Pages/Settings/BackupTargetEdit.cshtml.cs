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

        [Required(ErrorMessage = "Bitte einen Namen angeben.")]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        public bool IsDefault { get; set; }

        [Required(ErrorMessage = "Bitte den NAS-Host angeben.")]
        [Display(Name = "Host")]
        public string? SmbHost { get; set; }

        [Required(ErrorMessage = "Bitte die Freigabe angeben.")]
        [Display(Name = "Freigabe (Share)")]
        public string? SmbShare { get; set; }

        [Display(Name = "Unterverzeichnis")]
        public string? SmbDirectory { get; set; }

        [Display(Name = "Benutzer")]
        public string? SmbUsername { get; set; }

        [Display(Name = "Domäne")]
        public string? SmbDomain { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Passwort")]
        public string? SmbPassword { get; set; }
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
                SmbHost = t.SmbHost,
                SmbShare = t.SmbShare,
                SmbDirectory = t.SmbDirectory,
                SmbUsername = t.SmbUsername,
                SmbDomain = t.SmbDomain
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!ModelState.IsValid)
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

        return RedirectToPage("/Settings/Index");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var (ok, message) = await _targetService.TestInputAsync(ToInput(), Input.Id, HttpContext.RequestAborted);
        TestOk = ok;
        TestMessage = message;
        return Page();
    }

    private BackupTargetInput ToInput() => new()
    {
        Name = Input.Name,
        Type = BackupTargetType.Smb,
        IsDefault = Input.IsDefault,
        SmbHost = Input.SmbHost,
        SmbShare = Input.SmbShare,
        SmbDirectory = Input.SmbDirectory,
        SmbUsername = Input.SmbUsername,
        SmbDomain = Input.SmbDomain,
        SmbPassword = Input.SmbPassword
    };
}
