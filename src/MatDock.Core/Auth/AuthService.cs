using MatDock.Core.Data;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Auth;

/// <summary>Validates local username/password credentials.</summary>
public sealed class AuthService
{
    // Valid-format hash of "nothing useful": lets us always run one PBKDF2 verification even when the
    // user does not exist, so response timing does not reveal whether a username is valid.
    private const string DummyHash =
        "pbkdf2$sha256$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly MatDockDbContext _db;
    private readonly IPasswordHasher _passwordHasher;

    public AuthService(MatDockDbContext db, IPasswordHasher passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        // Generic message on purpose: never reveal whether the username exists.
        const string invalidCredentials = "Incorrect username or password.";

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

        // Always verify (against a dummy hash when the user is unknown) to equalize timing.
        var passwordValid = _passwordHasher.Verify(password, user?.PasswordHash ?? DummyHash);

        if (user is null || !passwordValid)
        {
            return AuthenticationResult.Failure(invalidCredentials);
        }

        // The "deactivated" hint is only shown once the password is correct, so it cannot be used to
        // enumerate usernames.
        if (!user.IsActive)
        {
            return AuthenticationResult.Failure("This account is disabled.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return AuthenticationResult.Success(user);
    }
}
