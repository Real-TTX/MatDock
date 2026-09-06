using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace MatDock.Core.Apps;

/// <summary>
/// MatDock "app" metadata for a compose stack — an icon, display name and extra action links. Read from
/// the <c>x-matdock:</c> extension block in the compose file and/or a <c>matdock.yml|yaml|json</c> sidecar
/// next to it. Persisted (resolved) as JSON on the stack so rendering needs no YAML parsing or file access.
/// </summary>
public sealed class AppMetadata
{
    public string? Name { get; set; }

    /// <summary>Icon: an http(s) URL, a data URL, a built-in icon name, or a single emoji/glyph.</summary>
    public string? Icon { get; set; }

    public string? Description { get; set; }

    public string? Category { get; set; }

    public List<AppAction> Actions { get; set; } = new();

    [YamlIgnore]
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Icon)
        && string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(Category)
        && Actions.Count == 0;
}

/// <summary>An extra action link shown for an app (e.g. "Admin" → /admin).</summary>
public sealed class AppAction
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute http(s) URL, or a "/relative" path resolved against the app host + a published port.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional icon (built-in name or emoji).</summary>
    public string? Icon { get; set; }

    /// <summary>Optional published host port a relative URL resolves against (else the app's primary port).</summary>
    public int? Port { get; set; }
}
