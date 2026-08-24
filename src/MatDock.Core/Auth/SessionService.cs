using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Auth;

/// <summary>
/// Manages server-side login sessions in SQLite. Because the sessions live on the mounted data
/// volume, they remain valid after a container restart (the client cookie only carries the token).
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly MatDockDbContext _db;
    private readonly MatDockOptions _options;

    public SessionService(MatDockDbContext db, IOptions<MatDockOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<UserSession> CreateAsync(long userId, string? userAgent, string? ipAddress, CancellationToken ct = default)
    {
        var session = new UserSession
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, _options.SessionLifetimeDays)),
            LastSeenAt = DateTime.UtcNow,
            UserAgent = Truncate(userAgent, 512),
            IpAddress = Truncate(ipAddress, 64)
        };

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Returns the session (with its user) if the token is valid, otherwise <c>null</c>.</summary>
    public async Task<UserSession?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var session = await _db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token, ct);

        if (session is null || session.ExpiresAt <= DateTime.UtcNow || session.User is null || !session.User.IsActive)
        {
            return null;
        }

        if (DateTime.UtcNow - session.LastSeenAt > TouchInterval)
        {
            // Slide the server-side expiry with activity (mirrors the cookie's sliding expiration).
            session.LastSeenAt = DateTime.UtcNow;
            session.ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, _options.SessionLifetimeDays));
            await _db.SaveChangesAsync(ct);
        }

        return session;
    }

    public async Task InvalidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == token, ct);
        if (session is not null)
        {
            _db.UserSessions.Remove(session); // soft delete
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
