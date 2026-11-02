using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Images;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// The safety rules of automatic updates: a new image waits its minimum age, a new image with vulnerabilities is held back (scanned by its exact reference
/// before anything is pulled), the labels of a container narrow the version tags, and the image's source names its release notes.
/// </summary>
public class UpdateGateTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private static string Trivy(int critical, int high)
        => "{\"Results\":[{\"Vulnerabilities\":["
           + string.Join(",", Enumerable.Range(0, critical).Select(i => $"{{\"VulnerabilityID\":\"CVE-C{i}\",\"Severity\":\"CRITICAL\",\"PkgName\":\"p{i}\"}}")
               .Concat(Enumerable.Range(0, high).Select(i => $"{{\"VulnerabilityID\":\"CVE-H{i}\",\"Severity\":\"HIGH\",\"PkgName\":\"q{i}\"}}")))
           + "]}]}";

    /// <summary>The rig's automation with a scanner over the same fake Docker (Trivy answers what <paramref name="report"/> says for an image).</summary>
    private static UpdateAutomation WithScanner(UpdateRig rig, Func<string, string?> report)
    {
        var options = Options.Create(new MatDockOptions());
        var environments = new EnvironmentService(rig.Db.Context, new FakeSecretProtector(), rig.Connection, options);
        var previous = rig.Hosts.Respond;
        rig.Hosts.Respond = c => c.Contains("aquasec/trivy", StringComparison.Ordinal) ? report(c) : previous?.Invoke(c);
        var scans = new ImageScanService(rig.Db.Context, rig.Hosts, environments, options);
        return new UpdateAutomation(environments, rig.Stacks, rig.Updates, rig.Policies, rig.Notes, rig.State, rig.Db.Context,
            NullLogger<UpdateAutomation>.Instance, scans);
    }

    [Fact]
    public async Task A_new_image_waits_its_minimum_age_before_it_is_applied()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; p.MinAgeHours = 24; p.CheckEveryHours = 1; });

        var first = await rig.RunAsync(T0);
        Assert.Equal(0, first.Applied);
        Assert.Contains(first.Lines, l => l.Contains("minimum age", StringComparison.Ordinal));
        Assert.Null(rig.Reload().ImageOverridesJson);
        Assert.Equal(T0, rig.Db.Context.UpdateNotices.AsNoTracking().Single().FirstSeenUtc);

        var tooSoon = await rig.RunAsync(T0.AddHours(23));
        Assert.Equal(0, tooSoon.Applied);

        var ripe = await rig.RunAsync(T0.AddHours(25));
        Assert.Equal(1, ripe.Applied);
        Assert.Equal("localhost:5000/app:2.0.0", ImageOverrides.Of(rig.Reload())["web"].Image);
    }

    [Fact]
    public async Task A_newer_image_during_the_wait_starts_the_clock_again()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; p.MinAgeHours = 24; p.CheckEveryHours = 1; });

        await rig.RunAsync(T0);
        rig.Tags.Add("2.1.0");
        var report = await rig.RunAsync(T0.AddHours(25)); // 2.0.0 is ripe, but 2.1.0 is the target now and was first seen just now
        Assert.Equal(0, report.Applied);
        Assert.Equal(1, (await rig.RunAsync(T0.AddHours(50))).Applied);
        Assert.Equal("localhost:5000/app:2.1.0", ImageOverrides.Of(rig.Reload())["web"].Image);
    }

    [Fact]
    public async Task A_new_image_with_critical_vulnerabilities_is_held_back_and_not_tried_again()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; p.VulnerabilityGate = VulnerabilityGate.Critical; p.CheckEveryHours = 1; });
        var scanned = new List<string>();
        var automation = WithScanner(rig, c => { scanned.Add(c); return Trivy(critical: 2, high: 0); });

        var report = await automation.RunAsync(T0, new DateTime(2026, 10, 22, 12, 0, 0));

        Assert.Equal(0, report.Applied);
        Assert.Contains(report.Lines, l => l.Contains("held back", StringComparison.Ordinal) && l.Contains("2 critical", StringComparison.Ordinal));
        Assert.Contains(scanned, c => c.Contains("'localhost:5000/app:2.0.0'", StringComparison.Ordinal)); // the new image, by its exact name
        Assert.Equal(0, rig.Count("up -d"));
        Assert.Contains(rig.Notes.Sent, n => n.Subject.Contains("Update held back", StringComparison.Ordinal));
        Assert.True(rig.Db.Context.UpdateNotices.AsNoTracking().Single().Failed);

        // Not tried again for the same image.
        scanned.Clear();
        var again = await automation.RunAsync(T0.AddHours(2), new DateTime(2026, 10, 22, 12, 0, 0));
        Assert.Equal(0, again.Applied);
        Assert.Empty(scanned);
    }

    [Fact]
    public async Task A_clean_new_image_passes_the_check_and_is_applied()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; p.VulnerabilityGate = VulnerabilityGate.CriticalHigh; });
        var automation = WithScanner(rig, _ => Trivy(critical: 0, high: 0));

        var report = await automation.RunAsync(T0, new DateTime(2026, 10, 22, 12, 0, 0));
        Assert.Equal(1, report.Applied);
    }

    [Fact]
    public async Task More_than_now_compares_with_the_image_that_runs()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; p.VulnerabilityGate = VulnerabilityGate.MoreThanCurrent; });
        // The running 1.2.0 has 3 high ones, the new 2.0.0 has 2: fewer, so it is applied.
        var automation = WithScanner(rig, c => c.Contains("app:1.2.0", StringComparison.Ordinal) ? Trivy(0, 3) : Trivy(0, 2));

        Assert.Equal(1, (await automation.RunAsync(T0, new DateTime(2026, 10, 22, 12, 0, 0))).Applied);
    }

    [Fact]
    public async Task A_scan_that_fails_holds_the_update_back()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; p.VulnerabilityGate = VulnerabilityGate.Any; });
        var automation = WithScanner(rig, _ => string.Empty);

        var report = await automation.RunAsync(T0, new DateTime(2026, 10, 22, 12, 0, 0));
        Assert.Equal(0, report.Applied);
        Assert.Contains(report.Lines, l => l.Contains("scan failed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_digest_update_is_scanned_by_the_new_digest()
    {
        var item = new PlannedUpdate("web", "nginx:latest", "abc", PlannedKind.Digest, "library/nginx@sha256:" + new string('a', 64));
        Assert.StartsWith("library/nginx@sha256:", item.ScanRef);
        Assert.True(ImageCommands.IsValidImageRef(item.ScanRef!));
    }

    [Fact]
    public async Task The_labels_of_the_container_narrow_the_version_tags()
    {
        using var rig = new UpdateRig();
        rig.Hosts.Containers.Clear();
        rig.Hosts.Containers.Add(System.Text.Json.JsonSerializer.Serialize(new
        {
            ID = "c0000000001", Names = "shop-web-1", Image = "localhost:5000/app:1.2.0", State = "running", Status = "Up",
            Labels = "com.docker.compose.project=shop,com.docker.compose.service=web,matdock.tag.exclude=^2\\.,org.opencontainers.image.source=https://github.com/acme/app",
            Ports = string.Empty,
        }));
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Latest; });

        var plan = await rig.Updates.PlanStackAsync(rig.Env, rig.Stack, (await rig.Policies.ResolveAsync(rig.Stack)), default);
        Assert.Equal("localhost:5000/app:1.3.0", Assert.Single(plan.Items).Target); // 2.0.0 is excluded by the label

        var report = await rig.Updates.CheckStackAsync(rig.Env, "shop");
        Assert.Equal("https://github.com/acme/app/releases", report.Images.Single().Changelog);
    }

    [Theory]
    [InlineData("matdock.tag.include", "^1\\.", "dockhand.tag.include", "^9", "^1\\.")]
    [InlineData("wud.tag.include", "^3", "dockhand.tag.include", "^4", "^4")]
    public void MatDocks_labels_win_over_dockhands_over_whats_up_dockers(string k1, string v1, string k2, string v2, string expected)
        => Assert.Equal(expected, UpdateLabels.Include(new Dictionary<string, string> { [k1] = v1, [k2] = v2 }));

    [Fact]
    public void The_filter_ignores_patterns_it_cannot_trust()
    {
        var tags = new[] { "1.0", "1.1-rc1", "2.0" };
        Assert.Equal(new[] { "1.0", "2.0" }, UpdateLabels.Filter(tags, null, "rc"));
        Assert.Equal(new[] { "1.0", "1.1-rc1" }, UpdateLabels.Filter(tags, "^1", null));
        Assert.Equal(tags, UpdateLabels.Filter(tags, "(unclosed", null));      // does not compile
        Assert.Equal(tags, UpdateLabels.Filter(tags, "${PATTERN}", null));     // a variable
        Assert.Equal(tags, UpdateLabels.Filter(tags, new string('a', 301), null));

        Assert.Equal("https://gitlab.com/acme/app/-/releases", UpdateLabels.Changelog(new Dictionary<string, string> { ["org.opencontainers.image.source"] = "https://gitlab.com/acme/app.git" }));
        Assert.Equal("https://notes.example/app", UpdateLabels.Changelog(new Dictionary<string, string>
        {
            ["matdock.changelog.url"] = "https://notes.example/app",
            ["org.opencontainers.image.source"] = "https://github.com/acme/app",
        }));
        Assert.Null(UpdateLabels.Changelog(new Dictionary<string, string> { ["org.opencontainers.image.source"] = "http://insecure.example/app" }));
    }

    [Fact]
    public void The_rule_checks_the_age_and_the_gate()
    {
        Assert.NotNull(UpdatePolicies.Problem(new UpdatePolicy { Scope = UpdatePolicyScope.Global, MinAgeHours = 721 }));
        Assert.NotNull(UpdatePolicies.Problem(new UpdatePolicy { Scope = UpdatePolicyScope.Global, VulnerabilityGate = (VulnerabilityGate)42 }));
        Assert.Null(UpdatePolicies.Problem(new UpdatePolicy { Scope = UpdatePolicyScope.Global, MinAgeHours = 48, VulnerabilityGate = VulnerabilityGate.Critical }));

        var resolved = UpdatePolicies.Resolve(new UpdatePolicy { MinAgeHours = 12, VulnerabilityGate = VulnerabilityGate.Any }, null, new UpdatePolicy { MinAgeHours = 0 });
        Assert.Equal(0, resolved.MinAgeHours);                      // the stack's own wins
        Assert.Equal(VulnerabilityGate.Any, resolved.VulnerabilityGate); // the global one where the stack says nothing
        Assert.False(UpdatePolicies.IsEmpty(new UpdatePolicy { MinAgeHours = 1 }));
    }
}
