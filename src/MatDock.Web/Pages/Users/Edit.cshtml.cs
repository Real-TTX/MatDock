using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Users;

public class EditModel : PageModel
{
    private readonly UserService _userService;

    public EditModel(UserService userService)
    {
        _userService = userService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Bitte einen Benutzernamen angeben.")]
        [StringLength(128)]
        [RegularExpression(@"^[A-Za-z0-9._@-]+$", ErrorMessage = "Nur Buchstaben, Zahlen und . _ @ - erlaubt.")]
        [Display(Name = "Benutzername")]
        public string Username { get; set; } = string.Empty;

        [StringLength(256)]
        [Display(Name = "Anzeigename")]
        public string? DisplayName { get; set; }

        [Display(Name = "Rolle")]
        public UserRole Role { get; set; } = UserRole.User;

        [Display(Name = "Aktiv")]
        public bool IsActive { get; set; } = true;

        [MinLength(8, ErrorMessage = "Das Passwort muss mindestens 8 Zeichen haben.")]
        [DataType(DataType.Password)]
        [Display(Name = "Passwort")]
        public string? Password { get; set; }

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        [Display(Name = "Passwort bestätigen")]
        public string? ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var user = await _userService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (user is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Role = user.Role,
                IsActive = user.IsActive
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsEdit && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Bitte ein Passwort vergeben.");
        }

        if (Input.Role == UserRole.Anonymous)
        {
            ModelState.AddModelError("Input.Role", "Die Rolle „Anonym\" ist Link-Freigaben vorbehalten.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var displayName = Input.DisplayName ?? string.Empty;

        if (IsEdit)
        {
            var updated = await _userService.UpdateAsync(
                Input.Id!.Value, displayName, Input.Role, Input.IsActive, Input.Password, HttpContext.RequestAborted);
            if (!updated)
            {
                return NotFound();
            }

            StatusMessage = "Benutzer gespeichert.";
        }
        else
        {
            if (await _userService.UsernameExistsAsync(Input.Username.Trim(), null, HttpContext.RequestAborted))
            {
                ModelState.AddModelError("Input.Username", "Dieser Benutzername ist bereits vergeben.");
                return Page();
            }

            await _userService.CreateAsync(Input.Username, displayName, Input.Password!, Input.Role, mustChangePassword: false, HttpContext.RequestAborted);
            StatusMessage = "Benutzer angelegt.";
        }

        IsError = false;
        return RedirectToPage("/Users/Index");
    }
}
