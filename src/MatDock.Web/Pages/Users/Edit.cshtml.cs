using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Users;
using MatDock.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Users;

public class EditModel : PageModel
{
    private readonly UserService _userService;
    private readonly EnvironmentService _environments;

    public EditModel(UserService userService, EnvironmentService environments)
    {
        _userService = userService;
        _environments = environments;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;

    /// <summary>The signed-in user edits their own account (which they cannot delete).</summary>
    public bool IsSelf => IsEdit && Input.Id == User.GetUserId();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    /// <summary>Every environment, for the choice of what a restricted user may use.</summary>
    public IReadOnlyList<DockerEnvironment> Environments { get; private set; } = Array.Empty<DockerEnvironment>();

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

        /// <summary>A user (not an admin) may use only the <see cref="EnvironmentIds"/>.</summary>
        public bool RestrictEnvironments { get; set; }

        public List<long> EnvironmentIds { get; set; } = new();

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
        Environments = await _environments.GetAllAsync(HttpContext.RequestAborted);
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
                IsActive = user.IsActive,
                RestrictEnvironments = user.RestrictEnvironments,
                EnvironmentIds = (await _userService.GetEnvironmentIdsAsync(user.Id, HttpContext.RequestAborted)).ToList(),
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Environments = await _environments.GetAllAsync(HttpContext.RequestAborted);
        if (!IsEdit && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Please set a password.");
        }

        if (Input.Role == UserRole.Anonymous)
        {
            ModelState.AddModelError("Input.Role", "Invalid role.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var displayName = Input.DisplayName ?? string.Empty;
        long userId;

        if (IsEdit)
        {
            var updated = await _userService.UpdateAsync(
                Input.Id!.Value, displayName, Input.Role, Input.IsActive, Input.Password, HttpContext.RequestAborted);
            if (!updated)
            {
                return NotFound();
            }

            userId = Input.Id.Value;
            StatusMessage = "User saved.";
        }
        else
        {
            if (await _userService.UsernameExistsAsync(Input.Username.Trim(), null, HttpContext.RequestAborted))
            {
                ModelState.AddModelError("Input.Username", "This username is already taken.");
                return Page();
            }

            userId = (await _userService.CreateAsync(Input.Username, displayName, Input.Password!, Input.Role, mustChangePassword: false, HttpContext.RequestAborted)).Id;
            StatusMessage = "User created.";
        }

        // Which environments the user may use (only a user of the role User can be limited; an admin never is).
        await _userService.SetEnvironmentAccessAsync(userId, Input.RestrictEnvironments && Input.Role == UserRole.User, Input.EnvironmentIds, HttpContext.RequestAborted);

        IsError = false;
        return RedirectToPage("/Users/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (User.GetUserId() == id)
        {
            StatusMessage = "You cannot delete your own account.";
            IsError = true;
            return RedirectToPage("Index");
        }

        var user = await _userService.GetAsync(id, HttpContext.RequestAborted);
        if (user is { Role: UserRole.Admin, IsActive: true } && await _userService.CountActiveAdminsAsync(HttpContext.RequestAborted) <= 1)
        {
            StatusMessage = "The last administrator cannot be deleted.";
            IsError = true;
            return RedirectToPage("Index");
        }

        var deleted = await _userService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "User deleted." : "User not found.";
        IsError = !deleted;
        return RedirectToPage("Index");
    }
}
