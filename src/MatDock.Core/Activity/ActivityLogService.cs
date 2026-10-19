using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Activity;

/// <summary>Writes and reads the activity log (who did what, across all environments).</summary>
public sealed class ActivityLogService
{
    /// <summary>How long entries are kept.</summary>
    public const int KeepDays = 90;

    private const int MaxText = 500;

    private readonly MatDockDbContext _db;

    public ActivityLogService(MatDockDbContext db) => _db = db;

    public async Task RecordAsync(string actor, string source, string action, string? target, string? environment, bool ok, string? message,
        CancellationToken ct = default)
    {
        _db.ActivityEntries.Add(new ActivityEntry
        {
            Actor = Cut(actor, 128) ?? string.Empty,
            Source = Cut(source, 10) ?? "ui",
            Action = Cut(action, 200) ?? string.Empty,
            Target = Cut(target, 300),
            Environment = Cut(environment, 128),
            Ok = ok,
            Message = Cut(message, MaxText),
        });
        await _db.SaveChangesAsync(ct);

        // Now and then (about every 100th entry) drop what is older than the retention.
        if (Random.Shared.Next(100) == 0)
        {
            await PruneAsync(DateTime.UtcNow.AddDays(-KeepDays), ct);
        }
    }

    /// <summary>Removes the entries older than <paramref name="before"/> for good; returns how many.</summary>
    public Task<int> PruneAsync(DateTime before, CancellationToken ct = default)
        => _db.ActivityEntries.IgnoreQueryFilters().Where(e => e.CreateDate < before).ExecuteDeleteAsync(ct);

    /// <summary>One page of the log, newest first, optionally filtered by a text (actor, action, target, environment, message)
    /// and to the failures.</summary>
    public async Task<(IReadOnlyList<ActivityEntry> Items, int Total)> QueryAsync(string? text, bool failedOnly, int page, int pageSize,
        CancellationToken ct = default)
    {
        var query = _db.ActivityEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim();
            query = query.Where(e => e.Actor.Contains(t) || e.Action.Contains(t) || (e.Target != null && e.Target.Contains(t))
                                     || (e.Environment != null && e.Environment.Contains(t)) || (e.Message != null && e.Message.Contains(t)));
        }

        if (failedOnly)
        {
            query = query.Where(e => !e.Ok);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.CreateDate).ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    private static string? Cut(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }
}
