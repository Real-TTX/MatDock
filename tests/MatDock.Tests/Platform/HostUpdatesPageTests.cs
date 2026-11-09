using System.Net;
using MatDock.Core.Operations;
using MatDock.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The hosts on the Updates page: what apt would upgrade on each host, looking again, updating Docker, and the dashboard's hint.</summary>
public sealed class HostUpdatesPageTests : IClassFixture<HostUpdatesPageTests.Site>
{
    private const string HostAnswer = """
        os|Debian GNU/Linux 13 (trixie)
        lists|1794193370
        Inst docker-ce-cli [5:29.9.2-1~debian.13~trixie] (5:29.9.3-1~debian.13~trixie Docker CE:trixie [amd64])
        Inst docker-ce [5:29.9.2-1~debian.13~trixie] (5:29.9.3-1~debian.13~trixie Docker CE:trixie [amd64])
        Inst vim-tiny [2:9.1.1230-2+deb13u1] (2:9.1.1230-2+deb13u2 Debian-Security:13/stable-security [amd64]) []
        """;

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("apt-host");
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public HostUpdateCache Cache => App.Services.GetRequiredService<HostUpdateCache>();

        public OperationRegistry Operations => App.Services.GetRequiredService<OperationRegistry>();

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public HostUpdatesPageTests(Site site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    private static async Task<string> PostAsync(HttpClient browser, string handler, long id)
    {
        var page = await (await browser.GetAsync("/Updates")).Content.ReadAsStringAsync();
        var data = new Dictionary<string, string> { ["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page) };
        var response = await browser.PostAsync($"/Updates?handler={handler}", new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private async Task<Operation> FinishedAsync(string location)
    {
        var id = System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("http://x"), location).Query)["op"];
        Assert.False(string.IsNullOrEmpty(id), $"no run started: {location}");
        for (var i = 0; i < 300; i++)
        {
            if (_site.Operations.Get(id!) is { IsFinished: true } op)
            {
                return op;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The run did not finish.");
    }

    [Fact]
    public async Task The_hosts_show_the_docker_update_the_security_fixes_and_the_age_of_the_lists()
    {
        _site.Cache.Set(_site.EnvId, HostUpdateReport.Parse(HostAnswer));
        var browser = await AdminAsync();

        var html = await (await browser.GetAsync("/Updates")).Content.ReadAsStringAsync();

        Assert.Contains("id=\"hosts\"", html);
        Assert.Contains("apt-host", html);
        Assert.Contains("Debian GNU/Linux 13 (trixie)", html);
        Assert.Contains("→ 29.9.3", html);
        Assert.Contains("3 packages", html);
        Assert.Contains("1 security", html);
        Assert.Contains("vim-tiny 2:9.1.1230-2+deb13u1 → 2:9.1.1230-2+deb13u2 (security)", WebUtility.HtmlDecode(html));
        Assert.Contains("handler=UpgradeDocker", html);

        var dashboard = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("host(s) with Docker or security updates", dashboard);
    }

    [Fact]
    public async Task Looking_at_a_host_asks_its_apt_through_a_read_only_helper_and_remembers_the_answer()
    {
        _site.App.Hosts.Respond = cmd => cmd.Contains("chroot /host", StringComparison.Ordinal) ? HostAnswer : null;
        try
        {
            var browser = await AdminAsync();
            var op = await FinishedAsync(await PostAsync(browser, "CheckHost", _site.EnvId));

            Assert.Null(op.Error);
            var check = Assert.Single(_site.App.Hosts.Commands, c => c.Contains("chroot /host", StringComparison.Ordinal) && c.Contains("REFRESH=1", StringComparison.Ordinal));
            Assert.Contains("-v /:/host:ro", check);
            Assert.Equal("29.9.3", _site.Cache.Get(_site.EnvId)!.Value.Report.DockerTarget);
        }
        finally
        {
            _site.App.Hosts.Respond = null;
        }
    }

    [Fact]
    public async Task Updating_docker_runs_apt_on_the_host_through_systemd_and_follows_its_log()
    {
        _site.Cache.Set(_site.EnvId, HostUpdateReport.Parse(HostAnswer));
        _site.App.Hosts.Respond = cmd =>
            cmd.Contains("/host/var/log/matdock/", StringComparison.Ordinal) ? "exit|0\nSetting up docker-ce (5:29.9.3-1~debian.13~trixie) ..." :
            cmd.Contains("chroot /host", StringComparison.Ordinal) ? "os|Debian GNU/Linux 13 (trixie)\nlists|1794193370\n" :
            null;
        try
        {
            var browser = await AdminAsync();
            var op = await FinishedAsync(await PostAsync(browser, "UpgradeDocker", _site.EnvId));

            Assert.Null(op.Error);
            var start = Assert.Single(_site.App.Hosts.Commands, c => c.Contains("systemd-run", StringComparison.Ordinal));
            Assert.Contains("--only-upgrade", start);
            Assert.Contains(" docker-ce-cli docker-ce; }", start);
            Assert.DoesNotContain("vim-tiny", start);
            Assert.Contains("Setting up docker-ce", op.Read(0).Text);
            Assert.Null(_site.Cache.Get(_site.EnvId)!.Value.Report.DockerTarget); // looked at again afterwards: nothing left
        }
        finally
        {
            _site.App.Hosts.Respond = null;
        }
    }

    [Fact]
    public async Task Updating_docker_without_a_known_update_does_nothing()
    {
        _site.Cache.Set(_site.EnvId, HostUpdateReport.Parse("os|Debian GNU/Linux 13 (trixie)\nlists|1794193370\n"));
        var browser = await AdminAsync();

        var location = await PostAsync(browser, "UpgradeDocker", _site.EnvId);

        Assert.DoesNotContain("op=", location);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains("systemd-run", StringComparison.Ordinal));
    }
}
