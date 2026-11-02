using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;

namespace MatDock.Core.Images;

/// <summary>The last scan of an image to download: CSV (a spreadsheet), JSON (the counts and the findings) or SARIF 2.1.0 (code scanning tools).</summary>
public static class ScanExport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Csv(ImageScan scan)
    {
        var text = new StringBuilder("id,severity,package,installed,fixed,title\n");
        foreach (var f in ImageScanService.Findings(scan))
        {
            text.Append(string.Join(',', new[] { f.Id, f.Severity, f.Package, f.Installed, f.Fixed ?? string.Empty, f.Title ?? string.Empty }.Select(Cell))).Append('\n');
        }

        return text.ToString();
    }

    public static string ToJson(ImageScan scan) => JsonSerializer.Serialize(new
    {
        image = scan.ImageRef,
        scannedAt = DateTime.SpecifyKind(scan.ScannedAt, DateTimeKind.Utc),
        counts = new { critical = scan.Critical, high = scan.High, medium = scan.Medium, low = scan.Low, unknown = scan.Unknown },
        findings = ImageScanService.Findings(scan),
    }, Json);

    /// <summary>SARIF 2.1.0: one rule per vulnerability, one result per finding (the level follows the severity), located at the image.</summary>
    public static string Sarif(ImageScan scan)
    {
        var findings = ImageScanService.Findings(scan);
        var rules = findings.DistinctBy(f => f.Id).Select(f => new
        {
            id = f.Id,
            shortDescription = new { text = f.Title ?? f.Id },
            helpUri = f.Id.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase) ? $"https://nvd.nist.gov/vuln/detail/{f.Id}" : null,
            properties = new { severity = f.Severity },
        });
        var results = findings.Select(f => new
        {
            ruleId = f.Id,
            level = f.Severity switch { "CRITICAL" or "HIGH" => "error", "MEDIUM" => "warning", _ => "note" },
            message = new { text = $"{f.Package} {f.Installed}: {f.Id} ({f.Severity}){(f.Fixed is null ? string.Empty : $", fixed in {f.Fixed}")}" },
            locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = scan.ImageRef } } } },
        });
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new[]
            {
                new
                {
                    tool = new { driver = new { name = "Trivy (via MatDock)", informationUri = "https://trivy.dev", rules } },
                    results,
                },
            },
        }, new JsonSerializerOptions(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>A CSV cell: quoted when it holds a comma, a quote or a line break; a leading = + - @ is defused (spreadsheet formulas).</summary>
    private static string Cell(string value)
    {
        var v = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
