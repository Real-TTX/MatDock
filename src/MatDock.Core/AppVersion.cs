using System.Reflection;

namespace MatDock.Core;

/// <summary>Exposes the running build's version string (set by the CI/versioning props).</summary>
public static class AppVersion
{
    /// <summary>
    /// The informational version, e.g. <c>0.1.42-20260824</c>, <c>nightly-42-20260824</c> or
    /// <c>local-20260824</c>. Falls back to the numeric assembly version if unset.
    /// </summary>
    public static string Informational { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
