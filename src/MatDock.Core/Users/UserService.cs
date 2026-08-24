using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Users;

/// <summary>CRUD and password management for local users.</summary>
public sealed class UserService
{
    private readonly MatDockDbContext _db;
    private readonly IPasswordHasher _passwordHasher;

    public UserService(MatDockDbContext db, IPasswordHasher passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public Task<List<User>> GetAllAsync(CancellationToken ct = default)
        => _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public Task<User?> GetAsync(long id, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

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
}
