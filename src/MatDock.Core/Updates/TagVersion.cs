using System.Text.RegularExpressions;

namespace MatDock.Core.Updates;

/// <summary>
/// A plain version tag: <c>1.2.3</c>, <c>v1.2</c>, <c>2024.10.1</c> (numbers and dots only, an optional leading <c>v</c>). Tags with a
/// suffix (<c>16-alpine</c>, <c>1.2.3-rc1</c>) or without a number (<c>latest</c>) are not versions here: guessing their order is
/// wrong too often, so nothing is suggested for them.
/// </summary>
public sealed partial class TagVersion : IComparable<TagVersion>
{
    [GeneratedRegex(@"^(?<v>v?)(?<n>\d{1,9}(\.\d{1,9}){1,3})\z")]
    private static partial Regex Pattern();

    private TagVersion(string tag, bool hasV, int[] parts)
    {
        Tag = tag;
        HasV = hasV;
        Parts = parts;
    }

    public string Tag { get; }
    public bool HasV { get; }
    public int[] Parts { get; }

    public static TagVersion? TryParse(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || Pattern().Match(tag) is not { Success: true } m)
        {
            return null;
        }

        return new TagVersion(tag, m.Groups["v"].Length > 0, m.Groups["n"].Value.Split('.').Select(int.Parse).ToArray());
    }

    /// <summary>Same notation as <paramref name="other"/> (a leading <c>v</c> or not, the same number of parts), so the two are comparable.</summary>
    public bool SameShape(TagVersion other) => HasV == other.HasV && Parts.Length == other.Parts.Length;

    public int CompareTo(TagVersion? other)
    {
        if (other is null) { return 1; }
        for (var i = 0; i < Math.Min(Parts.Length, other.Parts.Length); i++)
        {
            if (Parts[i] != other.Parts[i]) { return Parts[i].CompareTo(other.Parts[i]); }
        }

        return Parts.Length.CompareTo(other.Parts.Length);
    }

    /// <summary>The highest tag in <paramref name="tags"/> that is newer than <paramref name="current"/> and written the same way; null when there is none
    /// (or <paramref name="current"/> is not a version).</summary>
    public static string? Newest(string current, IEnumerable<string> tags)
    {
        if (TryParse(current) is not { } cur)
        {
            return null;
        }

        TagVersion? best = null;
        foreach (var t in tags)
        {
            if (TryParse(t) is { } v && v.SameShape(cur) && v.CompareTo(cur) > 0 && (best is null || v.CompareTo(best) > 0))
            {
                best = v;
            }
        }

        return best?.Tag;
    }
}
