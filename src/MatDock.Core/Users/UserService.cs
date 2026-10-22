using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatDock.Core.Users;

/// <summary>CRUD and password management for local users, and the users of the sign-in through MatIdentity.</summary>
public sealed class UserService
{
    private const string DisabledMessage = "This account is disabled in MatDock. Please contact an administrator.";

    private readonly MatDockDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<UserService> _logger;

    public UserService(MatDockDbContext db, IPasswordHasher passwordHasher, ILogger<UserService>? logger = null)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _logger = logger ?? NullLogger<UserService>.Instance;
    }

    public Task<List<User>> GetAllAsync(CancellationToken ct = default)
        => _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public Task<User?> GetAsync(long id, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>The environments granted to a restricted user (what the form shows; ignored for administrators and unrestricted users).</summary>
    public async Task<IReadOnlyList<long>> GetEnvironmentIdsAsync(long userId, CancellationToken ct = default)
        => await _db.UserEnvironments.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.EnvironmentId).ToListAsync(ct);

    /// <summary>For the user list: how many environments each <em>restricted</em> user may use (unrestricted users are not in the result).</summary>
    public async Task<IReadOnlyDictionary<long, int>> GetRestrictedCountsAsync(CancellationToken ct = default)
    {
        var restricted = await _db.Users.AsNoTracking().Where(u => u.RestrictEnvironments).Select(u => u.Id).ToListAsync(ct);
        var counts = await _db.UserEnvironments.AsNoTracking()
            .Where(x => restricted.Contains(x.UserId))
            .GroupBy(x => x.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return restricted.ToDictionary(id => id, id => counts.FirstOrDefault(c => c.Key == id)?.Count ?? 0);
    }

    /// <summary>
    /// Limits a user to the given environments (<paramref name="restrict"/>) or lifts the limit. The list is replaced as a whole; ids of environments
    /// that do not exist are dropped. Returns false for an unknown user. Only users of the role <see cref="UserRole.User"/> can be limited:
    /// an administrator is never restricted, so the flag is cleared for them.
    /// </summary>
    public async Task<bool> SetEnvironmentAccessAsync(long userId, bool restrict, IEnumerable<long> environmentIds, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        user.RestrictEnvironments = restrict && user.Role == UserRole.User;

        var requested = environmentIds.Distinct().ToList();
        var wanted = user.RestrictEnvironments
            ? (await _db.Environments.AsNoTracking().Where(e => requested.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet()
            : new HashSet<long>();

        var existing = await _db.UserEnvironments.Where(x => x.UserId == userId).ToListAsync(ct);
        _db.UserEnvironments.RemoveRange(existing.Where(x => !wanted.Contains(x.EnvironmentId)));
        var have = existing.Select(x => x.EnvironmentId).ToHashSet();
        foreach (var id in wanted.Where(id => !have.Contains(id)))
        {
            _db.UserEnvironments.Add(new UserEnvironment { UserId = userId, EnvironmentId = id });
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<bool> UsernameExistsAsync(string username, long? excludeId = null, CancellationToken ct = default)
        => _db.Users.AnyAsync(u => u.Username == username && (excludeId == null || u.Id != excludeId), ct);

    public async Task<User> CreateAsync(string username, string displayName, string password, UserRole role, bool mustChangePassword = false, CancellationToken ct = default)
    {
        var user = new User
        {
            Username = username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = _passwordHasher.Hash(password),
            Role = role,
            IsActive = true,
            MustChangePassword = mustChangePassword
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<bool> UpdateAsync(long id, string displayName, UserRole role, bool isActive, string? newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.Username : displayName.Trim();
        user.Role = role;
        user.IsActive = isActive;

        if (!string.IsNullOrEmpty(newPassword))
        {
            user.PasswordHash = _passwordHasher.Hash(newPassword);
            user.MustChangePassword = false;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        _db.Users.Remove(user); // soft delete
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sets a new password and clears the "must change" flag.</summary>
    public async Task<bool> ChangePasswordAsync(long id, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = _passwordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<int> CountAsync(CancellationToken ct = default)
        => _db.Users.CountAsync(ct);

    /// <summary>The number of active administrators.</summary>
    public Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
        => _db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct);

    /// <summary>
    /// Finds or creates the local user of a person that MatIdentity signed in (single sign-on, MatDesign docs/PLATFORM.md):
    /// (1) the user with this <c>sub</c>; a disabled or deleted one is refused,
    /// (2) else, when exactly one active user that is not linked yet and has a password has the person's user name (<c>preferred_username</c>, or the
    /// e-mail address; MatDock has no e-mail column): nothing is changed and the outcome asks for the link (<see cref="ExternalSignIn.LinkRequired"/>).
    /// A user name proves nothing (anyone may have registered with it), so the account is linked only after its password (<see cref="LinkExternalAsync"/>),
    /// (3) several matches, or a match that is disabled or linked to another <c>sub</c>, are refused (no second account beside it),
    /// (4) else a new user without a password (it can only sign in through MatIdentity).
    /// When <see cref="ExternalIdentity.Role"/> is <c>admin</c> or <c>user</c> the role follows it at every sign-in (never demoting the last
    /// administrator); without it the local role stays (MatDock manages its roles).
    /// </summary>
    public async Task<ExternalSignIn> SignInExternalAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignIn.Refuse("MatIdentity sent no subject (sub).");
        }

        var subject = identity.Subject.Trim();

        // Also a deleted user: its subject stays with it (unique index), and a deleted account does not come back through MatIdentity.
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct);
        if (user is not null && (user.UpdateState == UpdateState.Deleted || !user.IsActive))
        {
            return ExternalSignIn.Refuse(DisabledMessage);
        }

        var created = false;
        if (user is null)
        {
            var names = LoginNames(identity);
            var matches = names.Count == 0
                ? []
                : (await _db.Users.AsNoTracking().ToListAsync(ct))
                    .Where(u => names.Contains(u.Username.Trim().ToLowerInvariant()))
                    .ToList();

            if (matches.Count > 1)
            {
                _logger.LogWarning("MatIdentity subject {Subject} matches {Count} accounts by user name; refused", subject, matches.Count);
                return ExternalSignIn.Refuse("Several MatDock accounts match this person. Please ask an administrator.");
            }

            if (matches.Count == 1)
            {
                var match = matches[0];
                if (match.ExternalSubject is not null)
                {
                    _logger.LogWarning("MatIdentity subject {Subject} matches user {UserId}, which is linked with another subject; refused", subject, match.Id);
                    return ExternalSignIn.Refuse("The MatDock account with this name is linked with another person.");
                }

                if (!match.IsActive)
                {
                    return ExternalSignIn.Refuse(DisabledMessage);
                }

                if (string.IsNullOrEmpty(match.PasswordHash))
                {
                    return ExternalSignIn.Refuse("The MatDock account with this name cannot prove itself (it has no password).");
                }

                // Not linked by the name alone: the person proves the account with its password first.
                _logger.LogInformation("MatIdentity subject {Subject} matches user {UserId} by name; the link waits for its password", subject, match.Id);
                return ExternalSignIn.LinkRequired(match.Id);
            }

            user = new User
            {
                Username = await FreeUsernameAsync(identity, subject, ct),
                DisplayName = Clip(FirstNonEmpty(identity.Name, identity.PreferredUserName, identity.Email) ?? "MatIdentity", 256),
                PasswordHash = string.Empty,
                Role = UserRole.User,
                IsActive = true,
                ExternalSubject = subject,
            };
            _db.Users.Add(user);
            created = true;
        }

        await ApplyExternalRoleAsync(user, identity.Role, created, ct);
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (created)
        {
            _logger.LogInformation("User {Username} ({UserId}) was created at the first sign-in through MatIdentity", user.Username, user.Id);
        }

        return ExternalSignIn.SignedIn(user);
    }

    /// <summary>
    /// Links the user <paramref name="userId"/> with the person of <paramref name="identity"/> after the user proved the account with its password
    /// (the pending link of <see cref="SignInExternalAsync"/>): sets the external subject and follows the role claim like a sign-in through MatIdentity.
    /// Null (nothing changed) when the account is gone, disabled or linked meanwhile, or another user has the subject by now.
    /// </summary>
    public async Task<User?> LinkExternalAsync(long userId, ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return null;
        }

        var subject = identity.Subject.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        if (user.ExternalSubject is not null || await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.ExternalSubject == subject, ct))
        {
            _logger.LogWarning("User {UserId} was not linked with MatIdentity subject {Subject}: one of them is linked already", userId, subject);
            return null;
        }

        user.ExternalSubject = subject;
        await ApplyExternalRoleAsync(user, identity.Role, false, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {Username} ({UserId}) linked with MatIdentity subject {Subject} after its password", user.Username, user.Id, subject);
        return user;
    }

    /// <summary>The role claim of MatIdentity: <c>admin</c> or <c>user</c> sets the role (never demoting the last active administrator); anything else leaves it.</summary>
    private async Task ApplyExternalRoleAsync(User user, string? claim, bool created, CancellationToken ct)
    {
        UserRole? wanted = claim?.Trim().ToLowerInvariant() switch
        {
            "admin" => UserRole.Admin,
            "user" => UserRole.User,
            _ => null,
        };

        if (wanted is not { } role || user.Role == role)
        {
            return;
        }

        if (!created && user.Role == UserRole.Admin
            && !await _db.Users.AnyAsync(u => u.Id != user.Id && u.IsActive && u.Role == UserRole.Admin, ct))
        {
            _logger.LogWarning("MatIdentity asks for role {Role} for user {UserId}, who is the last administrator; the role stays", role, user.Id);
            return;
        }

        user.Role = role;
    }

    /// <summary>The names a local account of the person could have: <c>preferred_username</c> and the e-mail address, in small letters.</summary>
    private static HashSet<string> LoginNames(ExternalIdentity identity)
        => new[] { identity.PreferredUserName, identity.Email }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A user name for a new account: the person's name in MatIdentity, made unique with a number when needed.</summary>
    private async Task<string> FreeUsernameAsync(ExternalIdentity identity, string subject, CancellationToken ct)
    {
        var wanted = Clip(FirstNonEmpty(identity.PreferredUserName, identity.Email) ?? "matidentity-" + subject, 120);
        var name = wanted;
        for (var i = 2; await UsernameExistsAsync(name, null, ct); i++)
        {
            name = $"{wanted}-{i}";
        }

        return name;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Clip(string value, int max) => value.Length > max ? value[..max] : value;
}
