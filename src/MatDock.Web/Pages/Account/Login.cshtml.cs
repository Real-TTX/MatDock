using System.ComponentModel.DataAnnotations;
using MatDock.Core.Auth;
using MatDock.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AuthService _authService;
    private readonly SessionService _sessionService;

    public LoginModel(AuthService authService, SessionService sessionService)
    {
        _authService = authService;
        _sessionService = sessionService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Bitte Benutzernamen eingeben.")]
        [Display(Name = "Benutzername")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Bitte Passwort eingeben.")]
        [DataType(DataType.Password)]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Angemeldet bleiben")]
        public bool RememberMe { get; set; } = true;
    }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl());
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _authService.AuthenticateAsync(Input.Username, Input.Password, HttpContext.RequestAborted);
        if (!result.Succeeded || result.User is null)
        {
            ErrorMessage = result.Error ?? "Anmeldung fehlgeschlagen.";
            return Page();
        }

        var session = await _sessionService.CreateAsync(
            result.User.Id,
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.RequestAborted);

        var principal = PrincipalFactory.Build(result.User, session.Token);
        await HttpContext.SignInAsync(
            AuthConstants.CookieScheme,
            principal,
            new AuthenticationProperties { IsPersistent = Input.RememberMe });

        // Forced password change lands on the profile page via the guard middleware.
        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
