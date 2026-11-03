using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MatDock.Core.Stacks;

/// <summary>
/// Turns the JSON progress of <c>docker compose --progress json</c> (the only form that tells the size of a download without a terminal) back into the short
/// lines compose writes otherwise: <c>Image app Pulled</c>, and for a download <c>eb345aaee5d9 Downloading 23.07MB/83.91MB</c>, at most one per layer every
/// tenth of it, so the log stays readable and a page can show the download as a bar (<see cref="Operations.OperationSteps"/>). Other text passes as it is.
/// Fed in pieces as the output arrives (a line may be split); <see cref="Flush"/> writes what is left.
/// </summary>
public sealed class ComposeProgressLines(Action<string> write)
{
    private readonly StringBuilder _partial = new();
    private readonly Dictionary<string, int> _shownTenth = new(StringComparer.Ordinal);

    public void Write(string text)
    {
        _partial.Append(text);
        var all = _partial.ToString();
        var end = all.LastIndexOf('\n');
        if (end < 0)
        {
            // Not a JSON line in the making: plain output passes at once (a prompt, a progress without line ends).
            if (!all.TrimStart().StartsWith('{') || all.Length > 65536)
            {
                _partial.Clear();
                write(all);
            }

            return;
        }

        _partial.Clear().Append(all[(end + 1)..]);
        var output = new StringBuilder();
        foreach (var line in all[..end].Split('\n'))
        {
            if (Convert(line) is { } converted)
            {
                output.Append(converted).Append('\n');
            }
        }

        if (output.Length > 0)
        {
            write(output.ToString());
        }
    }

    public void Flush()
    {
        if (_partial.Length > 0)
        {
            var rest = _partial.ToString();
            _partial.Clear();
            write(Convert(rest) ?? string.Empty);
        }
    }

    /// <summary>The whole output of a command at once.</summary>
    public static string ConvertAll(string output)
    {
        var sb = new StringBuilder();
        var lines = new ComposeProgressLines(t => sb.Append(t));
        lines.Write(output);
        lines.Flush();
        return sb.ToString();
    }

    /// <summary>A line as compose would write it without JSON; null for a download step that is not worth a line.</summary>
    private string? Convert(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("{\"id\"", StringComparison.Ordinal))
        {
            return line;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            var what = root.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (id is null || what is null)
            {
                return line;
            }

            if (what == "Downloading" && root.TryGetProperty("current", out var c) && root.TryGetProperty("total", out var tot) && tot.GetDouble() > 0)
            {
                var tenth = (int)(c.GetDouble() * 10 / tot.GetDouble());
                if (_shownTenth.TryGetValue(id, out var shown) && shown >= tenth)
                {
                    return null;
                }

                _shownTenth[id] = tenth;
                return $" {id} Downloading {Size(c.GetDouble())}/{Size(tot.GetDouble())}";
            }

            if (what is "Downloading" or "Extracting" or "Waiting" or "Verifying Checksum")
            {
                return null; // the steps between: no line each
            }

            var details = root.TryGetProperty("details", out var d) ? d.GetString() : null;
            return string.IsNullOrEmpty(details) || details is "0B" or "1B" ? $" {id} {what}" : $" {id} {what} {details}";
        }
        catch (JsonException)
        {
            return line;
        }
    }

    private static string Size(double bytes)
        => bytes >= 1e9 ? (bytes / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "GB"
         : bytes >= 1e6 ? (bytes / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "MB"
         : (bytes / 1e3).ToString("0.00", CultureInfo.InvariantCulture) + "kB";
}
