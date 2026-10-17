using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MatDock.Core.Auth;
using MatDock.Core.Users;
using MatDock.Web.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Account;

/// <summary>
/// The local sign-in with user name and password. With MatIdentity configured (<c>MATIDENTITY_AUTHORITY</c>) a visit goes straight on to
/// MatIdentity; the local form stays reachable with <c>?local=1</c> (a way in for the administrator when MatIdentity is down), <c>?signedOut=1</c>
/// (back from the sign-out there) and <c>?ssoError=1</c> (the sign-in there failed) show the page without going there again.
/// After a sign-in through MatIdentity whose user name matches an account that is not linked yet, the page shows <c>?local=1&amp;link=1</c>:
/// a successful sign-in of exactly that account links it (<see cref="MatIdentityPendingLink"/>, <see cref="UserService.LinkExternalAsync"/>);
/// another account signs in without linking anything; Cancel deletes the pending link.
/// </summary>
[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AuthService _authService;
    private readonly SessionService _sessionService;
    private readonly UserService _users;
    private readonly MatPlatformSettings _platform;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(AuthService authService, SessionService sessionService, UserService users, MatPlatformSettings platform, ILogger<LoginModel> logger)
    {
        _authService = authService;
        _sessionService = sessionService;
        _users = users;
        _platform = platform;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    /// <summary><c>?local=1</c>: the local form even when MatIdentity is configured.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?signedOut=1</c>: back from the sign-out at MatIdentity, no automatic sign-in.</summary>
    public bool SignedOut { get; private set; }

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed or MatIdentity is not reachable.</summary>
    public bool SsoError { get; private set; }

    /// <summary>The pending link to an account with this name, when MatIdentity sent the person here to prove it (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    /// <summary>True when the sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => _platform.IdentityEnabled;

    /// <summary>The address that starts the sign-in at MatIdentity and comes back to <see cref="ReturnUrl"/>.</summary>
    public string IdentitySignInUrl => string.IsNullOrWhiteSpace(ReturnUrl)
        ? "/Account/Login"
        : "/Account/Login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    /// <summary>The address of the local form, with the same <see cref="ReturnUrl"/>.</summary>
    public string LocalSignInUrl => string.IsNullOrWhiteSpace(ReturnUrl)
        ? "/Account/Login?local=1"
        : "/Account/Login?local=1&returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your username.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Stay signed in")]
        public bool RememberMe { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        ReadFlags();

        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl());
        }

        // Auto login: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Local && !SignedOut && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = SafeReturnUrl() });
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                _logger.LogWarning(ex, "MatIdentity is not reachable, showing the local sign-in");
                Local = true;
                SsoError = true;
            }
        }

        if (PendingLink is not null && string.IsNullOrEmpty(Input.Username))
        {
            Input.Username = PendingLink.PreferredUserName ?? PendingLink.Email ?? string.Empty;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // A posted form is the local sign-in; on an error it shows the form again.
        ReadFlags();
        Local = true;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _authService.AuthenticateAsync(Input.Username, Input.Password, HttpContext.RequestAborted);
        if (!result.Succeeded || result.User is null)
        {
            ErrorMessage = result.Error ?? "Sign-in failed.";
            return Page();
        }

        var user = result.User;

        // A pending link of MatIdentity is only ever for the account that matched; it is deleted either way.
        string? idToken = null;
        if (MatIdentityPendingLink.TakeFor(HttpContext, user.Id.ToString(CultureInfo.InvariantCulture)) is { } pending)
        {
            var linked = await _users.LinkExternalAsync(
                user.Id,
                DockIdentityUsers.ToIdentity(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName, pending.Role),
                HttpContext.RequestAborted);
            if (linked is not null)
            {
                user = linked;
                idToken = pending.IdToken;
            }
            else
            {
                _logger.LogWarning("User {UserId} signed in, but was not linked with MatIdentity", user.Id);
            }
        }

        await DockIdentityUsers.SignInLocallyAsync(HttpContext, _sessionService, user, Input.RememberMe, idToken, HttpContext.RequestAborted);

        // Forced password change lands on the profile page via the guard middleware.
        return LocalRedirect(SafeReturnUrl());
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return LocalRedirect(LocalSignInUrl);
    }

    private void ReadFlags()
    {
        Local = QueryFlag("local");
        SignedOut = QueryFlag("signedOut");
        SsoError = QueryFlag("ssoError");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && QueryFlag("link");
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name)
        => Request.Query[name].ToString() is "1" or "true" or "True";

    private string SafeReturnUrl()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
