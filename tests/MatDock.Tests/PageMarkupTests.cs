using System.Text.RegularExpressions;
using Xunit;

namespace MatDock.Tests;

/// <summary>Rules for the markup of the pages, checked on the sources.</summary>
public partial class PageMarkupTests
{
    private static string PagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatDock.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("MatDock.slnx not found"), "src", "MatDock.Web", "Pages");
    }

    [GeneratedRegex(@"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandler();

    /// <summary>No inline event handlers: a value written into one (a file name with a quote) would run as script, because
    /// the browser decodes the HTML encoding of an attribute before it runs the handler. Behaviour lives in matdock.js.</summary>
    [Fact]
    public void Pages_have_no_inline_event_handlers()
    {
        var offenders = Directory.EnumerateFiles(PagesDirectory(), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => InlineHandler().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(offenders);
    }
}
