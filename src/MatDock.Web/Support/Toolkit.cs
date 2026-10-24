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
        MatUiVersion = ReadMatUiVersion(env);
        IconNames = SymbolRegex().Matches(Sprite).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Names = IconNames;
    }

    /// <summary>The version of the MatUi toolkit copy in <c>wwwroot/matui</c> (the first line of <c>matui.css</c> says it), or "unknown".</summary>
    public string MatUiVersion { get; }

    /// <summary>The icon names of the sprite, for code without the service (an app icon of the x-matdock metadata).</summary>
    public static IReadOnlySet<string> Names { get; private set; } = new HashSet<string>();

    /// <summary>The sprite as markup: a hidden <c>svg</c> with one <c>symbol</c> per icon (<c>mat-i-NAME</c>).</summary>
    public string Sprite { get; }

    public IReadOnlySet<string> IconNames { get; }

    private static string ReadMatUiVersion(IWebHostEnvironment env)
    {
        var file = env.WebRootFileProvider.GetFileInfo("matui/matui.css");
        if (!file.Exists)
        {
            return "unknown";
        }

        using var reader = new StreamReader(file.CreateReadStream());
        return VersionRegex().Match(reader.ReadLine() ?? string.Empty) is { Success: true } m ? m.Groups[1].Value : "unknown";
    }

    [GeneratedRegex(@"MatUi (\d+\.\d+\.\d+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();
}
