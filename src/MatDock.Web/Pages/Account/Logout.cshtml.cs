using MatDock.Core.Auth;
using MatDock.Web.Infrastructure;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Account;

/// <summary>Ends the session (server side and the cookie); after a sign-in through MatIdentity the session there ends too.</summary>
[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly SessionService _sessionService;
    private readonly MatPlatformSettings _platform;

    public LogoutModel(SessionService sessionService, MatPlatformSettings platform)
    {
        _sessionService = sessionService;
        _platform = platform;
    }

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        var token = User.GetSessionToken();

        // Read before the session is gone: only a sign-in through MatIdentity has one.
        var idToken = _platform.IdentityEnabled ? await _sessionService.GetIdTokenAsync(token, HttpContext.RequestAborted) : null;

        if (!string.IsNullOrEmpty(token))
        {
            await _sessionService.InvalidateAsync(token, HttpContext.RequestAborted);
        }

        await HttpContext.SignOutAsync(AuthConstants.CookieScheme);

        if (!string.IsNullOrEmpty(idToken))
        {
            // Then at MatIdentity as well; it sends the browser back to /signout-callback-oidc and on to /Account/Login?signedOut=1.
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, idToken);
        }

        return RedirectToPage("/Account/Login");
    }
}
