using System.Text.RegularExpressions;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Stacks;

/// <summary>The tags of the catalog (<see cref="Tag"/>) and the names stacks carry: create, recolour, rename (the stacks follow), delete (the stacks drop it).</summary>
public sealed partial class TagService
{
    public const int MaxLength = 32;
    public const int MaxPerStack = 20;

    private readonly MatDockDbContext _db;

    public TagService(MatDockDbContext db) => _db = db;

    public Task<List<Tag>> GetAllAsync(CancellationToken ct = default)
        => _db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    /// <summary>The names of a comma-separated list: trimmed, each at most <see cref="MaxLength"/>, no doubles (letters case-insensitively), at most
    /// <see cref="MaxPerStack"/>. A <c>:</c> and what follows (the colour of a label) is not part of the name.</summary>
    public static IReadOnlyList<string> Parse(string? csv)
    {
        var names = new List<string>();
        foreach (var raw in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.Split(':')[0].Trim();
            if (name.Length is 0 or > MaxLength || name.Any(char.IsControl) || names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(name);
            if (names.Count == MaxPerStack)
            {
                break;
            }
        }

        return names;
    }

    public static string? Join(IEnumerable<string> names) => names.Any() ? string.Join(", ", names) : null;

    /// <summary>The tags of a container's labels: <c>matdock.tags</c>, else <c>dockhand.tags</c> (<c>name[:colour[:icon]]</c>, comma-separated).</summary>
    public static IReadOnlyList<string> FromLabels(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null)
        {
            return [];
        }

        return Parse(labels.TryGetValue("matdock.tags", out var own) ? own : labels.TryGetValue("dockhand.tags", out var dockhand) ? dockhand : null);
    }

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColorRegex();

    public static bool IsColor(string? color) => color is not null && ColorRegex().IsMatch(color);

    /// <summary>Adds the names that the catalog does not have yet (without a colour).</summary>
    public async Task EnsureAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var wanted = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var existing = (await _db.Tags.Select(t => t.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted.Where(n => !existing.Contains(n)))
        {
            _db.Tags.Add(new Tag { Name = name });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Creates a tag or changes the colour of one of that name.</summary>
    public async Task<(bool Ok, string Message)> SaveAsync(string name, string? color, CancellationToken ct = default)
    {
        var clean = Parse(name).FirstOrDefault();
        if (clean is null || clean != name.Trim())
        {
            return (false, $"A tag is a name of up to {MaxLength} characters without a comma.");
        }

        if (!string.IsNullOrEmpty(color) && !IsColor(color))
        {
            return (false, "The colour is #rrggbb.");
        }

        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == clean, ct);
        if (tag is null)
        {
            _db.Tags.Add(new Tag { Name = clean, Color = string.IsNullOrEmpty(color) ? null : color.ToLowerInvariant() });
        }
        else
        {
            tag.Color = string.IsNullOrEmpty(color) ? null : color.ToLowerInvariant();
        }

        await _db.SaveChangesAsync(ct);
        return (true, $"Tag '{clean}' saved.");
    }

    /// <summary>Renames a tag; every stack that carries it follows.</summary>
    public async Task<(bool Ok, string Message)> RenameAsync(long id, string newName, CancellationToken ct = default)
    {
        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
        var clean = Parse(newName).FirstOrDefault();
        if (tag is null || clean is null)
        {
            return (false, tag is null ? "Tag not found." : $"A tag is a name of up to {MaxLength} characters without a comma.");
        }

        if (!string.Equals(tag.Name, clean, StringComparison.OrdinalIgnoreCase) && await _db.Tags.AnyAsync(t => t.Name == clean, ct))
        {
            return (false, $"A tag '{clean}' exists already.");
        }

        var old = tag.Name;
        tag.Name = clean;
        await ReplaceOnStacksAsync(old, clean, ct);
        await _db.SaveChangesAsync(ct);
        return (true, $"'{old}' is now '{clean}'.");
    }

    /// <summary>Deletes a tag from the catalog and from every stack.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null)
        {
            return false;
        }

        _db.Tags.Remove(tag);
        await ReplaceOnStacksAsync(tag.Name, null, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>How many stacks carry each tag (by name, case-insensitively).</summary>
    public async Task<Dictionary<string, int>> UsageAsync(CancellationToken ct = default)
    {
        var lists = await _db.Stacks.AsNoTracking().Where(s => s.Tags != null).Select(s => s.Tags).ToListAsync(ct);
        return lists.SelectMany(Parse).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task ReplaceOnStacksAsync(string old, string? replacement, CancellationToken ct)
    {
        var stacks = await _db.Stacks.Where(s => s.Tags != null).ToListAsync(ct);
        foreach (var stack in stacks)
        {
            var names = Parse(stack.Tags).ToList();
            var index = names.FindIndex(n => string.Equals(n, old, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            if (replacement is null || names.Contains(replacement, StringComparer.OrdinalIgnoreCase))
            {
                names.RemoveAt(index);
            }
            else
            {
                names[index] = replacement;
            }

            stack.Tags = Join(names);
        }
    }
}

/// <summary>What a stack's icon (<see cref="Stack.Icon"/>) may be.</summary>
public static partial class StackIcons
{
    /// <summary>The largest data URL (a logo of about 256 KB).</summary>
    public const int MaxDataUrl = 360_000;

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp|svg\+xml);base64,[A-Za-z0-9+/=]+\z")]
    private static partial Regex DataUrlRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}\z")]
    private static partial Regex NameRegex();

    /// <summary>Why a value cannot be an icon, or null: a toolkit icon name, a short glyph (an emoji), an https address or a data URL of an image.</summary>
    public static string? Problem(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return null;
        }

        if (icon.StartsWith("data:", StringComparison.Ordinal))
        {
            return icon.Length <= MaxDataUrl && DataUrlRegex().IsMatch(icon) ? null : "The image is too large (256 KB at most) or not PNG, JPEG, GIF, WebP or SVG.";
        }

        if (icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return icon.Length <= 1000 && Uri.TryCreate(icon, UriKind.Absolute, out _) ? null : "Not a valid https address.";
        }

        return NameRegex().IsMatch(icon) || (icon.Length <= 8 && !icon.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\''))
            ? null
            : "An icon is an icon name, an emoji, an https address or an uploaded image.";
    }
}
