namespace MatDock.Web.Infrastructure;

/// <summary>Names for the authentication scheme and the custom claims MatDock stores in the cookie.</summary>
public static class AuthConstants
{
    public const string CookieScheme = "MatDock";

    public const string SessionTokenClaim = "matdock:session-token";
    public const string DisplayNameClaim = "matdock:display-name";
    public const string MustChangePasswordClaim = "matdock:must-change-password";
}
