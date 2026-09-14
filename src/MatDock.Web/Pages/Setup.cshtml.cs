using System.ComponentModel.DataAnnotations;
using MatDock.Core.Auth;
using MatDock.Core.Entities;
using MatDock.Core.Schedules;
using MatDock.Core.Users;
using MatDock.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages;

/// <summary>First-run wizard: creates the first administrator and optionally sets up maintenance
/// schedules. Reachable only while the database has no users (see <see cref="SetupGuardMiddleware"/>).</summary>
[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly UserService _users;
    private readonly ScheduleService _schedules;
    private readonly SessionService _sessions;
    private readonly SetupState _state;

    public SetupModel(UserService users, ScheduleService schedules, SessionService sessions, SetupState state)
    {
        _users = users;
        _schedules = schedules;
        _sessions = sessions;
        _state = state;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please choose a username.")]
        public string Username { get; set; } = "admin";

        public string? DisplayName { get; set; } = "Administrator";

        [Required(ErrorMessage = "Please choose a password.")]
        [MinLength(8, ErrorMessage = "Use at least 8 characters.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;

        // Optional maintenance schedules.
        public bool PruneImages { get; set; } = true;
        public bool PruneVolumes { get; set; }
        public bool PruneNetworks { get; set; }

        /// <summary>daily | weekly | monthly</summary>
        public string Frequency { get; set; } = "weekly";
    }

    public async Task<IActionResult> OnGetAsync()
        => await AlreadyDoneAsync() ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        // Guard against a second admin being created once setup is done.
        if (await AlreadyDoneAsync())
        {
            return RedirectToPage("/Index");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Input.Password != Input.ConfirmPassword)
        {
            ErrorMessage = "The passwords do not match.";
            return Page();
        }

        var username = Input.Username.Trim();
        if (await _users.UsernameExistsAsync(username, ct: HttpContext.RequestAborted))
        {
            ErrorMessage = "That username is already taken.";
            return Page();
        }

        var user = await _users.CreateAsync(username, Input.DisplayName ?? username, Input.Password,
            UserRole.Admin, mustChangePassword: false, HttpContext.RequestAborted);
        _state.Completed = true;

        // Optional maintenance schedules (weekly by default), one ScheduledTask per selected action.
        var cron = Input.Frequency switch
        {
            "daily" => "0 3 * * *",
            "monthly" => "0 3 1 * *",
            _ => "0 3 * * 0",
        };
        await MaybeCreateAsync(Input.PruneImages, "Prune images", ScheduleAction.PruneImages, cron);
        await MaybeCreateAsync(Input.PruneVolumes, "Prune volumes", ScheduleAction.PruneVolumes, cron);
        await MaybeCreateAsync(Input.PruneNetworks, "Prune networks", ScheduleAction.PruneNetworks, cron);

        // Sign the new administrator in and land on the dashboard.
        var session = await _sessions.CreateAsync(user.Id, Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), HttpContext.RequestAborted);
        var principal = PrincipalFactory.Build(user, session.Token);
        await HttpContext.SignInAsync(AuthConstants.CookieScheme, principal,
            new AuthenticationProperties { IsPersistent = true });

        return RedirectToPage("/Index");
    }

    private async Task MaybeCreateAsync(bool selected, string name, ScheduleAction action, string cron)
    {
        if (!selected)
        {
            return;
        }

        await _schedules.CreateAsync(new ScheduleInput
        {
            Name = name,
            Enabled = true,
            Trigger = ScheduleTrigger.Cron,
            Cron = cron,
            Action = action,
        }, HttpContext.RequestAborted);
    }

    private async Task<bool> AlreadyDoneAsync()
        => _state.Completed || await _users.CountAsync(HttpContext.RequestAborted) > 0;
}
