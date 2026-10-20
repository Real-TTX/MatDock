using System.Text.RegularExpressions;

namespace MatDock.Web.Support;

/// <summary>
/// What the pages need of the MatUi toolkit as text: the icon sprite, which every page inlines once (rule ICON-01), and the names in it.
/// The files themselves (<c>wwwroot/matui</c>) are a copy of the toolkit (rule CODE-03); <c>scripts/update-matui.ps1</c> brings a newer one.
/// </summary>
public sealed partial class Toolkit
{
    public Toolkit(IWebHostEnvironment env)
    {
        var file = env.WebRootFileProvider.GetFileInfo("matui/matui-icons.svg");
        using var reader = new StreamReader(file.CreateReadStream());
        Sprite = reader.ReadToEnd();
        IconNames = SymbolRegex().Matches(Sprite).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Names = IconNames;
    }

    /// <summary>The icon names of the sprite, for code without the service (an app icon of the x-matdock metadata).</summary>
    public static IReadOnlySet<string> Names { get; private set; } = new HashSet<string>();

    /// <summary>The sprite as markup: a hidden <c>svg</c> with one <c>symbol</c> per icon (<c>mat-i-NAME</c>).</summary>
    public string Sprite { get; }

    public IReadOnlySet<string> IconNames { get; }

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();
}
