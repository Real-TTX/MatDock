namespace MatDock.Core.Apps;

/// <summary>Helpers for app icons: detecting a file reference and turning an icon file into a data URL.</summary>
public static class AppIcons
{
    private const int MaxBytes = 512 * 1024;

    /// <summary>Conventional icon file names to look for next to a compose file when none is specified.</summary>
    public static readonly string[] DefaultFileNames =
        { "icon.svg", "icon.png", "icon.jpg", "icon.jpeg", "icon.webp" };

    /// <summary>True when the icon value looks like a repo file (not a URL, data URL or icon-name/emoji).</summary>
    public static bool IsFileName(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)
            || icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A file reference is anything with a supported image extension (possibly in a subfolder).
        return ContentType(icon) is not null;
    }

    /// <summary>Encodes icon bytes as a data URL, or null if empty/too large/unsupported type.</summary>
    public static string? ToDataUrl(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return null;
        }

        var ct = ContentType(fileName);
        return ct is null ? null : $"data:{ct};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        _ => null,
    };
}
