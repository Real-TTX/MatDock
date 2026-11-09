using MatDock.Core.Updates;
using Xunit;

namespace MatDock.Tests;

public class HostUpdatesTests
{
    // Real output of HostUpdateCommands.Check on a Debian 13 host with Docker's repository (server1, 09.11.2026).
    private const string Server1Output = """
        os|Debian GNU/Linux 13 (trixie)
        lists|1794193370
        Inst docker-ce-cli [5:29.9.2-1~debian.13~trixie] (5:29.9.3-1~debian.13~trixie Docker CE:trixie [amd64])
        Inst docker-ce [5:29.9.2-1~debian.13~trixie] (5:29.9.3-1~debian.13~trixie Docker CE:trixie [amd64])
        Inst linux-image-6.12.116+deb13-amd64 (6.12.116-1 Debian-Security:13/stable-security [amd64])
        Inst vim-tiny [2:9.1.1230-2+deb13u1] (2:9.1.1230-2+deb13u2 Debian-Security:13/stable-security [amd64]) []
        Inst libext2fs2t64 [1.47.2-3+b11] (1.47.2-3+b12 Debian:13.7/stable [amd64]) [e2fsprogs:amd64 on libext2fs2t64:amd64] [e2fsprogs:amd64 ]
        Inst docker-ce-rootless-extras [5:29.9.2-1~debian.13~trixie] (5:29.9.3-1~debian.13~trixie Docker CE:trixie [amd64])
        Inst tzdata [2026b-0+deb13u1] (2026f-0+deb13u1 Debian:13-updates/stable-updates [all])
        """;

    [Fact]
    public void Parse_reads_packages_docker_and_security()
    {
        var report = HostUpdateReport.Parse(Server1Output);

        Assert.Null(report.Error);
        Assert.True(report.AptAvailable);
        Assert.Equal("Debian GNU/Linux 13 (trixie)", report.OsName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794193370).UtcDateTime, report.ListsUpdatedUtc);
        Assert.False(report.RebootRequired);
        Assert.Equal(7, report.Packages.Count);

        Assert.Equal(["docker-ce-cli", "docker-ce", "docker-ce-rootless-extras"], report.DockerPackages.Select(p => p.Name));
        Assert.Equal("29.9.3", report.DockerTarget);
        Assert.Equal(2, report.SecurityCount);

        var kernel = report.Packages.Single(p => p.Name.StartsWith("linux-image", StringComparison.Ordinal));
        Assert.True(kernel.IsNew);
        Assert.True(kernel.IsSecurity);
        Assert.Equal("6.12.116-1", kernel.To);

        var vim = report.Packages.Single(p => p.Name == "vim-tiny");
        Assert.Equal("2:9.1.1230-2+deb13u1", vim.From);
        Assert.Equal("2:9.1.1230-2+deb13u2", vim.To);
        Assert.Equal("Debian-Security:13/stable-security", vim.Origin);

        var ext2 = report.Packages.Single(p => p.Name == "libext2fs2t64");
        Assert.Equal("Debian:13.7/stable", ext2.Origin);
        Assert.False(ext2.IsSecurity);

        Assert.Equal("Debian:13-updates/stable-updates", report.Packages.Single(p => p.Name == "tzdata").Origin);
    }

    [Fact]
    public void Parse_reads_an_up_to_date_host_and_a_pending_reboot()
    {
        var report = HostUpdateReport.Parse("os|Ubuntu 24.04.3 LTS\nlists|1794193370\nreboot|1\n");

        Assert.Empty(report.Packages);
        Assert.True(report.RebootRequired);
        Assert.Null(report.DockerTarget);
        Assert.Equal("Ubuntu 24.04.3 LTS", report.OsName);
    }

    [Fact]
    public void Parse_knows_hosts_without_apt()
    {
        var report = HostUpdateReport.Parse("os|Alpine Linux v3.22\nnoapt|\n");

        Assert.False(report.AptAvailable);
        Assert.Null(report.Error);
        Assert.False(report.ListsStale(DateTime.UtcNow));
    }

    [Fact]
    public void Parse_keeps_apt_errors_and_a_silent_host()
    {
        var withError = HostUpdateReport.Parse("os|Debian GNU/Linux 13 (trixie)\nlists|1794193370\nE: Could not get lock /var/lib/dpkg/lock-frontend\n");
        Assert.Equal("Could not get lock /var/lib/dpkg/lock-frontend", withError.Error);

        var silent = HostUpdateReport.Parse("", "docker: Cannot connect to the Docker daemon");
        Assert.Equal("docker: Cannot connect to the Docker daemon", silent.Error);
        Assert.Empty(silent.Packages);
    }

    [Fact]
    public void Lists_older_than_two_days_are_stale()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var fresh = HostUpdateReport.Parse($"os|x\nlists|{new DateTimeOffset(now.AddHours(-20)).ToUnixTimeSeconds()}\n");
        var old = HostUpdateReport.Parse($"os|x\nlists|{new DateTimeOffset(now.AddDays(-76)).ToUnixTimeSeconds()}\n");
        var unknown = HostUpdateReport.Parse("os|x\nlists|\n");

        Assert.False(fresh.ListsStale(now));
        Assert.True(old.ListsStale(now));
        Assert.True(unknown.ListsStale(now));
    }

    [Theory]
    [InlineData("5:29.9.3-1~debian.13~trixie", "29.9.3")]
    [InlineData("5:29.9.3-1~ubuntu.24.04~noble", "29.9.3")]
    [InlineData("26.1.5+dfsg1-9+b9", "26.1.5")]
    [InlineData("27.5.1-0ubuntu3~24.04.2", "27.5.1")]
    [InlineData("2.4.2-3~debian.13~trixie", "2.4.2")]
    public void ShortVersion_matches_what_docker_version_says(string debian, string expected)
        => Assert.Equal(expected, HostUpdateReport.ShortVersion(debian));

    [Fact]
    public void Check_reads_the_host_read_only_with_writable_lists_and_tmp()
    {
        var cmd = HostUpdateCommands.Check("busybox", "sudo -n docker");

        Assert.Contains("sudo -n docker run --rm --log-driver none --network host", cmd);
        Assert.Contains(" -v /:/host:ro ", cmd);
        Assert.Contains("-v /var/lib/apt/lists:/host/var/lib/apt/lists", cmd);
        Assert.Contains("-v /var/cache/apt:/host/var/cache/apt", cmd);
        Assert.Contains("--tmpfs /host/tmp", cmd);
        Assert.Contains("-e REFRESH=1", cmd);
        Assert.Contains("'busybox' chroot /host /bin/sh -c '", cmd);
        Assert.Contains("LC_ALL=C", cmd);
        Assert.Contains("apt-get -s -o Debug::NoLocking=1 dist-upgrade", cmd);
        Assert.DoesNotContain("--privileged", cmd);

        Assert.Contains("-e REFRESH=0", HostUpdateCommands.Check("busybox", refresh: false));
        Assert.Throws<ArgumentException>(() => HostUpdateCommands.Check("busybox; rm -rf /"));
    }

    [Fact]
    public void StartUpgrade_hands_the_upgrade_to_systemd_on_the_host()
    {
        var unit = HostUpdateCommands.NewUnitName();
        var cmd = HostUpdateCommands.StartUpgrade(["docker-ce", "docker-ce-cli", "containerd.io"], unit, "busybox");

        Assert.Contains("docker run --rm --log-driver none --privileged --pid=host 'busybox' nsenter -t 1 -m -u -i -n -- systemd-run --quiet --collect --unit " + unit, cmd);
        Assert.Contains("apt-get update -q && apt-get install -y -q --only-upgrade", cmd);
        Assert.Contains("--force-confold", cmd);
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", cmd);
        Assert.Contains(" docker-ce docker-ce-cli containerd.io; }", cmd);
        Assert.Contains($">/var/log/matdock/{unit}.log 2>&1; echo $? >/var/log/matdock/{unit}.exit", cmd);
    }

    [Theory]
    [InlineData("docker-ce;reboot")]
    [InlineData("$(id)")]
    [InlineData("-o")]
    [InlineData("Docker-CE")]
    public void StartUpgrade_rejects_unsafe_package_names(string package)
        => Assert.Throws<ArgumentException>(() => HostUpdateCommands.StartUpgrade([package], HostUpdateCommands.NewUnitName(), "busybox"));

    [Fact]
    public void StartUpgrade_rejects_foreign_units_and_an_empty_list()
    {
        Assert.Throws<ArgumentException>(() => HostUpdateCommands.StartUpgrade(["docker-ce"], "ssh", "busybox"));
        Assert.Throws<ArgumentException>(() => HostUpdateCommands.StartUpgrade(Array.Empty<string>(), HostUpdateCommands.NewUnitName(), "busybox"));
    }

    [Fact]
    public void NewUnitName_is_unique_and_valid()
    {
        var a = HostUpdateCommands.NewUnitName();
        Assert.Matches(@"^matdock-host-update-[0-9a-f]{12}$", a);
        Assert.NotEqual(a, HostUpdateCommands.NewUnitName());
        Assert.True(HostUpdateCommands.IsValidUnitName(a));
    }

    [Fact]
    public void ReadUpgrade_reads_the_log_read_only_and_ParseUpgrade_splits_exit_and_log()
    {
        var unit = HostUpdateCommands.NewUnitName();
        var cmd = HostUpdateCommands.ReadUpgrade(unit, "busybox", tailLines: 50);
        Assert.Contains("-v /var/log:/host/var/log:ro", cmd);
        Assert.Contains($"/host/var/log/matdock/{unit}.exit", cmd);
        Assert.Contains("tail -n 50", cmd);

        Assert.Equal((null, "Reading package lists..."), HostUpdateCommands.ParseUpgrade("Reading package lists...\n"));
        var (exit, log) = HostUpdateCommands.ParseUpgrade("exit|0\nSetting up docker-ce (5:29.9.3-1~debian.13~trixie) ...\n");
        Assert.Equal(0, exit);
        Assert.Equal("Setting up docker-ce (5:29.9.3-1~debian.13~trixie) ...", log);
    }
}

public class HostUpdateSummaryTests
{
    private static HostUpdateReport Report(string body) => HostUpdateReport.Parse("os|Debian GNU/Linux 13 (trixie)\nlists|1794193370\n" + body);

    private const string DockerAndSecurity =
        "Inst docker-ce [5:29.7.2-1~debian.13~trixie] (5:29.9.3-1~debian.13~trixie Docker CE:trixie [amd64])\n" +
        "Inst zlib1g [1:1.3.dfsg+really1.3.1-1+b1] (1:1.3.dfsg+really1.3.1-1+deb13u1 Debian-Security:13/stable-security [amd64])\n" +
        "Inst bash [5.2.37-2+b9] (5.2.37-2+b10 Debian:13.7/stable [amd64])\n";

    [Fact]
    public void Summary_names_docker_packages_security_and_reboot()
    {
        Assert.Equal("Docker 29.7.2 → 29.9.3 · 3 packages, 1 security · reboot required", Report(DockerAndSecurity + "reboot|1\n").Summary("29.7.2"));
        Assert.Equal("Docker 29.9.3 · 3 packages, 1 security", Report(DockerAndSecurity).Summary(null));
        Assert.Equal("Up to date.", Report("").Summary("29.9.3"));
        Assert.Equal("No apt on this host.", HostUpdateReport.Parse("os|Alpine\nnoapt|\n").Summary(null));
        Assert.Equal("boom", HostUpdateReport.Failed("boom").Summary(null));
    }

    [Fact]
    public void The_same_pending_updates_are_announced_once()
    {
        var cache = new HostUpdateCache();
        var pending = Report(DockerAndSecurity);

        Assert.True(cache.ShouldAnnounce(1, pending));
        Assert.False(cache.ShouldAnnounce(1, pending));
        Assert.False(cache.ShouldAnnounce(1, HostUpdateReport.Failed("host down"))); // a failed look forgets nothing
        Assert.False(cache.ShouldAnnounce(1, pending));
        Assert.True(cache.ShouldAnnounce(2, pending)); // another environment

        var newerDocker = Report(DockerAndSecurity.Replace("5:29.9.3", "5:29.10.0", StringComparison.Ordinal));
        Assert.True(cache.ShouldAnnounce(1, newerDocker));

        Assert.False(cache.ShouldAnnounce(1, Report("Inst bash [5.2.37-2+b9] (5.2.37-2+b10 Debian:13.7/stable [amd64])\n"))); // nothing worth a message
        Assert.True(cache.ShouldAnnounce(1, newerDocker)); // after it was cleared, the next one is news again
    }
}
