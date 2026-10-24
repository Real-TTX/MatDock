using System.Globalization;

namespace MatDock.Web.Support;

/// <summary>
/// The selection of a list (<c>data-selectable</c>): every ticked row sends <c>ids</c> = <c>{envId}|{key}</c> (the key is a container id, an image
/// reference, a volume or network name; none of them holds a <c>|</c>) with the form of the bulk bar, which names the action.
/// </summary>
public static class Bulk
{
    /// <summary>More rows than this in one go are refused (the bar is for a handful, a prune for everything).</summary>
    public const int MaxItems = 200;

    public static string Value(long envId, string key) => envId.ToString(CultureInfo.InvariantCulture) + "|" + key;

    /// <summary>The rows of a selection, by environment; values that are not <c>{id}|{key}</c> are dropped.</summary>
    public static IReadOnlyList<(long EnvId, string Key)> Parse(IEnumerable<string>? values)
        => (values ?? [])
            .Select(v => v.Split('|', 2))
            .Where(p => p.Length == 2 && long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) && p[1].Length is > 0 and <= 512)
            .Select(p => (long.Parse(p[0], CultureInfo.InvariantCulture), p[1]))
            .Distinct()
            .Take(MaxItems)
            .ToList();

    /// <summary>"Stop: 3 done." or "Stop: 2 done, 1 failed (web: is not running)." from the result of each row.</summary>
    public static (string Message, bool Failed) Summary(string verb, IReadOnlyList<(string Name, bool Ok, string Message)> results)
    {
        if (results.Count == 0)
        {
            return ("Nothing selected.", true);
        }

        var failed = results.Where(r => !r.Ok).ToList();
        var done = results.Count - failed.Count;
        var text = failed.Count == 0
            ? $"{verb}: {done} done."
            : $"{verb}: {done} done, {failed.Count} failed ({string.Join("; ", failed.Take(5).Select(f => $"{f.Name}: {f.Message}"))}{(failed.Count > 5 ? "; …" : string.Empty)}).";
        return (text, failed.Count > 0);
    }
}

/// <summary>The bar above a list that shows while rows are ticked: how many, and a button per action (each submits the form <see cref="FormId"/>).</summary>
public sealed record BulkBar(string FormId, IReadOnlyList<BulkButton> Buttons);

/// <summary>One action of a <see cref="BulkBar"/>; <see cref="Confirm"/> asks first.</summary>
public sealed record BulkButton(string Action, string Icon, string Label, bool Danger = false, string? Confirm = null);
