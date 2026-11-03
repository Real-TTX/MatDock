using System.Text.RegularExpressions;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Stacks;

/// <summary>
/// The deploy history of the managed stacks (<see cref="StackDeployment"/>): a row when a deploy starts, its end with what compose said. Writing it
/// never fails a deploy (a failing save is logged). The newest <see cref="Keep"/> per stack stay.
/// </summary>
public sealed partial class DeploymentHistory
{
    /// <summary>How many deploys of one stack are kept.</summary>
    public const int Keep = 50;

    /// <summary>How much of compose's output is kept per deploy (the end of it).</summary>
    public const int MaxLog = 200_000;

    private readonly MatDockDbContext _db;
    private readonly ILogger<DeploymentHistory> _logger;

    public DeploymentHistory(MatDockDbContext db, ILogger<DeploymentHistory> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>The deploys of a stack, the newest first.</summary>
    public Task<List<StackDeployment>> GetAsync(long stackId, int take = Keep, CancellationToken ct = default)
        => _db.StackDeployments.AsNoTracking().Where(d => d.StackId == stackId)
            .OrderByDescending(d => d.StartedAt).ThenByDescending(d => d.Id).Take(take).ToListAsync(ct);

    /// <summary>The last deploy of every stack of the list (the newest one each), by stack id.</summary>
    public async Task<Dictionary<long, StackDeployment>> LastAsync(IReadOnlyCollection<long> stackIds, CancellationToken ct = default)
    {
        if (stackIds.Count == 0)
        {
            return [];
        }

        var rows = await _db.StackDeployments.AsNoTracking().Where(d => stackIds.Contains(d.StackId))
            .Select(d => new { d.Id, d.StackId, d.StartedAt, d.Ok, d.Trigger, d.Summary, d.FinishedAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.StackId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).First())
            .ToDictionary(r => r.StackId, r => new StackDeployment
            {
                Id = r.Id, StackId = r.StackId, StartedAt = r.StartedAt, Ok = r.Ok, Trigger = r.Trigger, Summary = r.Summary, FinishedAt = r.FinishedAt,
            });
    }

    /// <summary>Records that a deploy of <paramref name="stack"/> starts; null when the row could not be written.</summary>
    public async Task<StackDeployment?> StartAsync(Stack stack, DeployRequest request, DeployOptions options, CancellationToken ct = default)
    {
        var row = new StackDeployment
        {
            StackId = stack.Id,
            StackName = stack.Name,
            EnvironmentId = stack.EnvironmentId,
            Trigger = Short(request.Trigger, 20) ?? DeployTrigger.Manual,
            Actor = Short(request.Actor, 200),
            Options = Short(options.Describe(), 200),
            StartedAt = DateTime.UtcNow,
        };

        try
        {
            _db.StackDeployments.Add(row);
            await SaveAsync(ct);
            return row;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Stack {Name}: the start of a deploy could not be recorded.", stack.Name);
            _db.Entry(row).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>Records the end of a deploy started with <see cref="StartAsync"/> and trims the stack's history to <see cref="Keep"/>.</summary>
    public async Task FinishAsync(StackDeployment? row, bool ok, string? output, string? commitSha = null)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            row.FinishedAt = DateTime.UtcNow;
            row.Ok = ok;
            row.Log = Tail(output);
            row.Summary = Short(Summarize(ok, output), 500);
            row.CommitSha = Short(commitSha, 64) ?? row.CommitSha;
            await SaveAsync(CancellationToken.None);
            await TrimAsync(row.StackId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stack {Name}: the end of a deploy could not be recorded.", row.StackName);
        }
    }

    /// <summary>The summary of a deploy of MatDock's own stack while the helper container finishes it (<see cref="SelfHandover"/>).</summary>
    public const string HandedOverSummary = "Handed over: MatDock restarts itself, a helper container finishes the deploy.";

    /// <summary>
    /// Records that a deploy of MatDock's own stack was handed to the helper container: it stays open (no end, no result) with what ran so far, until
    /// <see cref="CompleteHandoverAsync"/> after the restart adds what the helper said.
    /// </summary>
    public async Task HandOverAsync(StackDeployment? row, string? output, string? commitSha = null)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            row.Log = Tail(output);
            row.Summary = HandedOverSummary;
            row.CommitSha = Short(commitSha, 64) ?? row.CommitSha;
            await SaveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stack {Name}: the handover of a deploy could not be recorded.", row.StackName);
        }
    }

    /// <summary>The deploys handed to a helper container that have no result yet.</summary>
    public Task<List<StackDeployment>> HandedOverAsync(CancellationToken ct = default)
        => _db.StackDeployments.Where(d => d.Ok == null && d.Summary == HandedOverSummary).OrderBy(d => d.StartedAt).ToListAsync(ct);

    /// <summary>Ends a handed-over deploy with the helper's result (<paramref name="helperOutput"/> is compose's output in the helper; null = none came).</summary>
    public async Task CompleteHandoverAsync(StackDeployment row, bool ok, string? helperOutput)
    {
        try
        {
            row.FinishedAt = DateTime.UtcNow;
            row.Ok = ok;
            row.Log = Tail((row.Log ?? string.Empty) + "\n\n--- helper container ---\n" + (helperOutput ?? "(no output)"));
            row.Summary = Short(helperOutput is null ? "The helper container left no result." : Summarize(ok, helperOutput), 500);
            await SaveAsync(CancellationToken.None);
            await TrimAsync(row.StackId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stack {Name}: the end of a handed-over deploy could not be recorded.", row.StackName);
        }
    }

    private async Task TrimAsync(long stackId)
    {
        var stale = await _db.StackDeployments.Where(d => d.StackId == stackId)
            .OrderByDescending(d => d.StartedAt).ThenByDescending(d => d.Id).Skip(Keep).ToListAsync(CancellationToken.None);
        if (stale.Count > 0)
        {
            _db.StackDeployments.RemoveRange(stale);
            await SaveAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Saves, and tries again a few times when SQLite answers "database is locked": the deploy runs in the background and writes while a page
    /// (its activity entry, a list) writes too, and SQLite has one writer at a time.
    /// </summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < 10 && ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 })
            {
                _logger.LogDebug("Saving the deploy history: the database is locked (attempt {Attempt}).", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 100 * attempt)), ct);
            }
        }
    }

    /// <summary>
    /// One line about a deploy: for a success what compose did to the containers ("2 recreated, 1 started", from its progress lines such as
    /// <c>Container web-1  Recreated</c>), for a failure the last line that names the problem.
    /// </summary>
    public static string Summarize(bool ok, string? output)
    {
        var lines = (output ?? string.Empty).Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!ok)
        {
            var reason = lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase))
                         ?? lines.LastOrDefault();
            return string.IsNullOrEmpty(reason) ? "Failed." : reason;
        }

        // Per container what matters most of what compose reported (its progress repeats: Creating, Created, Starting, Started): a
        // re-created container was also started, so it counts as re-created; one that only reports Running did not change.
        var outcome = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = ProgressLine().Match(line);
            if (m.Success)
            {
                var rank = Rank(m.Groups["state"].Value);
                var name = m.Groups["name"].Value;
                outcome[name] = Math.Min(outcome.GetValueOrDefault(name, int.MaxValue), rank);
            }
        }

        var pulled = lines.Count(l => PulledLine().IsMatch(l));
        var built = lines.Count(l => BuiltLine().IsMatch(l));
        var parts = outcome.Values.GroupBy(r => r).OrderBy(g => g.Key).Select(g => $"{g.Count()} {States[g.Key]}").ToList();
        if (pulled > 0) { parts.Insert(0, $"{pulled} pulled"); }
        if (built > 0) { parts.Insert(0, $"{built} built"); }
        if (parts.Count == 0)
        {
            return "Deployed.";
        }

        return string.Join(", ", parts) + ".";
    }

    /// <summary>The words of the summary, the most telling first (<see cref="Rank"/> is the index).</summary>
    private static readonly string[] States = ["removed", "recreated", "created", "started", "stopped", "unchanged"];

    private static int Rank(string state) => state.ToLowerInvariant() switch
    {
        "removed" => 0,
        "recreated" => 1,
        "created" => 2,
        "started" => 3,
        "stopped" => 4,
        _ => 5, // running, waiting, healthy: there already
    };

    // " ✔ Container shop-web-1  Recreated   0.4s" (the tick and the time may be missing)
    [GeneratedRegex(@"Container\s+(?<name>\S+)\s+(?<state>Created|Recreated|Started|Stopped|Removed|Running|Waiting|Healthy)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressLine();

    // " ✔ web Pulled   2.1s" (with a terminal) or " Image nginx:1 Pulled" (without one, compose 5) — one per image that was fetched
    [GeneratedRegex(@"^\W*(?:Image\s+)?\S+\s+Pulled\b", RegexOptions.IgnoreCase)]
    private static partial Regex PulledLine();

    // " ✔ Image shop-web:local  Built" (compose v2.30+), " ✔ Service web  Built" (v2.20+) or "Successfully built" (the classic builder)
    [GeneratedRegex(@"(\b(Service|Image)\s+\S+\s+Built\b|^Successfully built\b)", RegexOptions.IgnoreCase)]
    private static partial Regex BuiltLine();

    private static string? Tail(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        return output.Length <= MaxLog ? output : "…" + output[^MaxLog..];
    }

    private static string? Short(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
