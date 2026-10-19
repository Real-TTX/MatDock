using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Images;

/// <summary>One known vulnerability of an image.</summary>
public sealed record ImageFinding(string Id, string Severity, string Package, string Installed, string? Fixed, string? Title);

/// <summary>What a scan found: the counts per severity and the most severe findings.</summary>
public sealed record ImageScanSummary(int Critical, int High, int Medium, int Low, int Unknown, IReadOnlyList<ImageFinding> Findings)
{
    public int Total => Critical + High + Medium + Low + Unknown;
}

/// <summary>
/// Scans images for known vulnerabilities with Trivy, run as a throwaway container on the image's own Docker host (it reads the image through that
/// host's Docker socket; its vulnerability database is kept in the volume <see cref="CacheVolume"/> there, the first scan downloads it). The result
/// of the last scan of every image is kept per environment.
/// </summary>
public sealed class ImageScanService
{
    public const string CacheVolume = "matdock-trivy-cache";

    /// <summary>The findings kept per scan (the most severe first); the counts are always complete.</summary>
    public const int MaxFindings = 200;

    private static readonly string[] Severities = ["CRITICAL", "HIGH", "MEDIUM", "LOW", "UNKNOWN"];

    private readonly MatDockDbContext _db;
    private readonly IHostSessionFactory _sessions;
    private readonly EnvironmentService _environments;
    private readonly MatDockOptions _options;

    public ImageScanService(MatDockDbContext db, IHostSessionFactory sessions, EnvironmentService environments, IOptions<MatDockOptions> options)
    {
        _db = db;
        _sessions = sessions;
        _environments = environments;
        _options = options.Value;
    }

    /// <summary>The Trivy run for <paramref name="reference"/>: JSON on stdout, vulnerabilities only.</summary>
    public static string ScanCommand(string dockerHead, string scannerImage, string reference)
    {
        if (!ImageCommands.IsValidImageRef(reference) || !ImageCommands.IsValidImageRef(scannerImage))
        {
            throw new ArgumentException($"Invalid image reference: '{reference}'.", nameof(reference));
        }

        return VolumeCommands.PathPrefix + dockerHead + " run --rm -v /var/run/docker.sock:/var/run/docker.sock:ro -v " + CacheVolume + ":/root/.cache/ "
               + VolumeFileCommands.ShellQuote(scannerImage) + " image --quiet --format json --scanners vuln --timeout 20m "
               + VolumeFileCommands.ShellQuote(reference);
    }

    /// <summary>Reads Trivy's JSON report.</summary>
    public static ImageScanSummary Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var counts = Severities.ToDictionary(s => s, _ => 0);
        var findings = new List<ImageFinding>();
        if (doc.RootElement.TryGetProperty("Results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("Vulnerabilities", out var vulns) || vulns.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var v in vulns.EnumerateArray())
                {
                    var severity = (Str(v, "Severity") ?? "UNKNOWN").ToUpperInvariant();
                    if (!counts.ContainsKey(severity))
                    {
                        severity = "UNKNOWN";
                    }

                    counts[severity]++;
                    findings.Add(new ImageFinding(Str(v, "VulnerabilityID") ?? "?", severity, Str(v, "PkgName") ?? "?", Str(v, "InstalledVersion") ?? "",
                        Str(v, "FixedVersion"), Str(v, "Title")));
                }
            }
        }

        var ordered = findings
            .DistinctBy(f => (f.Id, f.Package))
            .OrderBy(f => Array.IndexOf(Severities, f.Severity))
            .ThenBy(f => f.Fixed is null)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .Take(MaxFindings)
            .ToList();
        return new ImageScanSummary(counts["CRITICAL"], counts["HIGH"], counts["MEDIUM"], counts["LOW"], counts["UNKNOWN"], ordered);
    }

    /// <summary>Scans <paramref name="reference"/> on <paramref name="env"/> and keeps the result. Long (minutes on the first run).</summary>
    public async Task<(bool Ok, string Message, ImageScanSummary? Summary)> ScanAsync(DockerEnvironment env, string reference, CancellationToken ct = default, Action<string>? log = null)
    {
        if (!ImageCommands.IsValidImageRef(reference))
        {
            return (false, "Invalid image reference.", null);
        }

        var settings = _environments.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        string output, error;
        int exit;
        try
        {
            log?.Invoke($"==> Trivy scans {reference} on {env.Name} (the first scan downloads the vulnerability database)");
            using var session = _sessions.Create(settings);
            await session.ConnectAsync(ct);
            using var cmd = session.CreateCommand(ScanCommand(head, _options.ScannerImage, reference));
            cmd.CommandTimeout = TimeSpan.FromMinutes(30);
            await cmd.ExecuteAsync(ct);
            (exit, output, error) = (cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return await SaveAsync(env, reference, null, ex.Message, ct);
        }

        if (exit != 0 || string.IsNullOrWhiteSpace(output))
        {
            var reason = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {exit}";
            return await SaveAsync(env, reference, null, $"The scan failed: {reason}", ct);
        }

        ImageScanSummary summary;
        try
        {
            summary = Parse(output);
        }
        catch (JsonException ex)
        {
            return await SaveAsync(env, reference, null, $"Trivy answered no report: {ex.Message}", ct);
        }

        return await SaveAsync(env, reference, summary, null, ct);
    }

    private async Task<(bool, string, ImageScanSummary?)> SaveAsync(DockerEnvironment env, string reference, ImageScanSummary? summary, string? error, CancellationToken ct)
    {
        var scan = await _db.ImageScans.FirstOrDefaultAsync(s => s.EnvironmentId == env.Id && s.ImageRef == reference, ct);
        if (scan is null)
        {
            scan = new ImageScan { EnvironmentId = env.Id, ImageRef = reference };
            _db.ImageScans.Add(scan);
        }

        scan.ScannedAt = DateTime.UtcNow;
        scan.Error = error?.Length > 500 ? error[..500] : error;
        if (summary is not null)
        {
            (scan.Critical, scan.High, scan.Medium, scan.Low, scan.Unknown) = (summary.Critical, summary.High, summary.Medium, summary.Low, summary.Unknown);
            scan.FindingsJson = JsonSerializer.Serialize(summary.Findings);
        }

        await _db.SaveChangesAsync(ct);
        return summary is null
            ? (false, error ?? "The scan failed.", null)
            : (true, $"{reference}: {Describe(summary)}.", summary);
    }

    public static string Describe(ImageScanSummary s)
        => s.Total == 0 ? "no known vulnerabilities" : $"{s.Critical} critical, {s.High} high, {s.Medium} medium, {s.Low} low";

    public Task<ImageScan?> GetAsync(long environmentId, string reference, CancellationToken ct = default)
        => _db.ImageScans.AsNoTracking().FirstOrDefaultAsync(s => s.EnvironmentId == environmentId && s.ImageRef == reference, ct);

    /// <summary>The last scans of the environments (all when <paramref name="environmentId"/> is null), by environment and image.</summary>
    public async Task<IReadOnlyDictionary<(long EnvironmentId, string ImageRef), ImageScan>> GetLatestAsync(long? environmentId, CancellationToken ct = default)
        => (await _db.ImageScans.AsNoTracking().Where(s => environmentId == null || s.EnvironmentId == environmentId).ToListAsync(ct))
            .ToDictionary(s => (s.EnvironmentId, s.ImageRef));

    public static IReadOnlyList<ImageFinding> Findings(ImageScan scan)
    {
        if (string.IsNullOrEmpty(scan.FindingsJson))
        {
            return Array.Empty<ImageFinding>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ImageFinding>>(scan.FindingsJson) ?? new List<ImageFinding>();
        }
        catch (JsonException)
        {
            return Array.Empty<ImageFinding>();
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
