using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatDock.Web.Support;

/// <summary>Small helpers for the markup of the MatUi toolkit: icons from the sprite, initials, how the registry knows MatDock.</summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite. The name must exist in <c>matui-icons.svg</c> (a test checks the pages).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>The logo tile of the toolkit with MatDock's logo in it (the file brings its own dark tile).</summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString($"<span class=\"mat-logo mat-logo--{size}\" role=\"img\" aria-label=\"{App.Name}\"><img src=\"/img/logo.svg\" alt=\"\" /></span>");

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string name)
        => string.Concat(name.Split([' ', '-', '_', '.', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>How the toolkit's registry (MatDesign AppRegistry) knows MatDock; the accent itself comes from <c>data-app</c> in <c>matui.css</c>.</summary>
    public static class App
    {
        public const string Id = "matdock";
        public const string Name = "MatDock";
        public const string Tagline = "Advanced Docker management";

        /// <summary>The accent of the registry; it colours the bar of the browser. A test compares it with <c>matui.css</c>.</summary>
        public const string Accent = "#1d78be";
    }
}
