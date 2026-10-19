using MatDock.Core.Images;
using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class ImageScanTests
{
    private const string Report = """
        {"SchemaVersion":2,"ArtifactName":"postgres:16","Results":[
          {"Target":"debian","Vulnerabilities":[
            {"VulnerabilityID":"CVE-1","PkgName":"openssl","InstalledVersion":"3.0.1","FixedVersion":"3.0.2","Severity":"CRITICAL","Title":"Bad"},
            {"VulnerabilityID":"CVE-2","PkgName":"zlib","InstalledVersion":"1.2","Severity":"HIGH"},
            {"VulnerabilityID":"CVE-3","PkgName":"apt","InstalledVersion":"3.0","Severity":"LOW"},
            {"VulnerabilityID":"CVE-3","PkgName":"apt","InstalledVersion":"3.0","Severity":"LOW"},
            {"VulnerabilityID":"CVE-4","PkgName":"x","InstalledVersion":"1","Severity":"WEIRD"}]},
          {"Target":"usr/local/bin/gosu","Vulnerabilities":[
            {"VulnerabilityID":"CVE-5","PkgName":"stdlib","InstalledVersion":"1.18","FixedVersion":"1.19","Severity":"HIGH"}]},
          {"Target":"empty"}]}
        """;

    [Fact]
    public void The_report_is_counted_and_the_worst_come_first()
    {
        var s = ImageScanService.Parse(Report);
        Assert.Equal((1, 2, 0, 2, 1), (s.Critical, s.High, s.Medium, s.Low, s.Unknown));
        Assert.Equal(["CVE-1", "CVE-5", "CVE-2", "CVE-3", "CVE-4"], s.Findings.Select(f => f.Id));
        Assert.Equal("3.0.2", s.Findings[0].Fixed);
        Assert.Equal("1 critical, 2 high, 0 medium, 2 low", ImageScanService.Describe(s));
        Assert.Equal("no known vulnerabilities", ImageScanService.Describe(ImageScanService.Parse("""{"Results":[]}""")));
    }

    [Fact]
    public void Trivy_runs_on_the_images_host_with_its_cache_volume()
    {
        Assert.Equal(VolumeCommands.PathPrefix + "docker run --rm -v /var/run/docker.sock:/var/run/docker.sock:ro -v matdock-trivy-cache:/root/.cache/ 'aquasec/trivy:latest' image --quiet --format json --scanners vuln --timeout 20m 'postgres:16'",
            ImageScanService.ScanCommand("docker", "aquasec/trivy:latest", "postgres:16"));
        Assert.Throws<ArgumentException>(() => ImageScanService.ScanCommand("docker", "aquasec/trivy:latest", "x; reboot"));
    }
}
