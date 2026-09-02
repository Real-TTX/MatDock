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

        [Required(ErrorMessage = "Please enter a username.")]
        [StringLength(128)]
        [RegularExpression(@"^[A-Za-z0-9._@-]+$", ErrorMessage = "Only letters, digits and . _ @ - are allowed.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [StringLength(256)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Display(Name = "Role")]
        public UserRole Role { get; set; } = UserRole.User;

        [Display(Name = "Active")]
        public bool IsActive { get; set; } = true;

        [MinLength(8, ErrorMessage = "The password must be at least 8 characters.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string? Password { get; set; }

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "Confirm password")]
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
            ModelState.AddModelError("Input.Password", "Please set a password.");
        }

        if (Input.Role == UserRole.Anonymous)
        {
            ModelState.AddModelError("Input.Role", "The \"Anonymous\" role is reserved for link shares.");
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

            StatusMessage = "User saved.";
        }
        else
        {
            if (await _userService.UsernameExistsAsync(Input.Username.Trim(), null, HttpContext.RequestAborted))
            {
                ModelState.AddModelError("Input.Username", "This username is already taken.");
                return Page();
            }

            await _userService.CreateAsync(Input.Username, displayName, Input.Password!, Input.Role, mustChangePassword: false, HttpContext.RequestAborted);
            StatusMessage = "User created.";
        }

        IsError = false;
        return RedirectToPage("/Users/Index");
    }
}
