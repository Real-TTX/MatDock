using System.Runtime.InteropServices;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.About;

/// <summary>What this MatDock is: its version and build, and the parts it is made of. Reached through the version at the bottom of the menu.</summary>
public class IndexModel : PageModel
{
    private readonly Toolkit _toolkit;

    public IndexModel(Toolkit toolkit) => _toolkit = toolkit;

    public string Version { get; } = MatDock.Core.AppVersion.Informational;

    /// <summary>Release, Nightly or Local, from the shape of the version (<c>1.4.2-20261010</c>, <c>nightly-12-20261010</c>, <c>local-20261010</c>).</summary>
    public string Channel => Version.StartsWith("nightly-", StringComparison.Ordinal) ? "Nightly"
        : Version.StartsWith("local-", StringComparison.Ordinal) ? "Local"
        : "Release";

    /// <summary>The build date written at the end of the version (yyyyMMdd), or null when there is none.</summary>
    public DateOnly? BuildDate
        => Version.Split('-').LastOrDefault() is { Length: 8 } last && DateOnly.TryParseExact(last, "yyyyMMdd", out var date) ? date : null;

    public string MatUiVersion => _toolkit.MatUiVersion;
    public string MatPlatformVersion => global::MatPlatform.MatPlatformInfo.Version;
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string System => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
}
