using System.Net;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The page of one environment: what its engine says about itself, the disk usage, and the cleanup (admins only, volumes never).</summary>
public sealed class SystemViewTests : IClassFixture<SystemViewTests.Site>
{
    private const string Info = """
        {"ServerVersion":"27.3.1","Name":"docker-01","OperatingSystem":"Debian GNU/Linux 12 (bookworm)","KernelVersion":"6.1.0-26-amd64","Architecture":"x86_64",
         "NCPU":8,"MemTotal":33554432000,"Driver":"overlay2","DockerRootDir":"/var/lib/docker","LoggingDriver":"json-file","CgroupDriver":"systemd","CgroupVersion":"2",
         "DefaultRuntime":"runc","Runtimes":{"runc":{},"io.containerd.runc.v2":{}},"SecurityOptions":["name=apparmor","name=seccomp,profile=builtin","name=cgroupns"],
         "Containers":12,"ContainersRunning":9,"ContainersPaused":0,"ContainersStopped":3,"Images":40,"Swarm":{"LocalNodeState":"inactive"},
         "Warnings":["WARNING: No swap limit support"]}
        """;

    private const string Version = """{"Client":{"Version":"27.3.1"},"Server":{"Version":"27.3.1","ApiVersion":"1.47"}}""";

    private const string DiskUsage = """
        {"Active":"9","Reclaimable":"2.1GB (40%)","Size":"5.3GB","TotalCount":"40","Type":"Images"}
        {"Active":"9","Reclaimable":"12MB (3%)","Size":"400MB","TotalCount":"12","Type":"Containers"}
        {"Active":"6","Reclaimable":"0B (0%)","Size":"1.1GB","TotalCount":"8","Type":"Local Volumes"}
        {"Active":"0","Reclaimable":"800MB","Size":"800MB","TotalCount":"31","Type":"Build Cache"}
        """;

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("engine-host");
            OtherEnvId = App.AddLocalEnvironment("other-host");
            App.Hosts.Respond = command =>
                command.Contains(" info --format", StringComparison.Ordinal) ? Info
                : command.Contains(" version --format '{{json .}}'", StringComparison.Ordinal) ? Version
                : command.Contains(" compose version --short", StringComparison.Ordinal) ? "2.29.7\n"
                : command.Contains(" system df ", StringComparison.Ordinal) ? DiskUsage
                : command.Contains(" builder prune", StringComparison.Ordinal) ? "Total:\t800MB\n"
                : null;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }
        public long OtherEnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public SystemViewTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(UserRole role = UserRole.Admin, params long[] onlyEnvironments)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        if (role == UserRole.Admin)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
            return browser;
        }

        var name = "sys" + Guid.NewGuid().ToString("N")[..8];
        var id = SignIn.AddUser(_site.App, name, "UserPass123!", role);
        if (onlyEnvironments.Length > 0)
        {
            _site.App.Db(db =>
            {
                db.Users.Single(u => u.Id == id).RestrictEnvironments = true;
                db.UserEnvironments.AddRange(onlyEnvironments.Select(e => new UserEnvironment { UserId = id, EnvironmentId = e }));
                return db.SaveChanges();
            });
        }

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        return browser;
    }

    [Fact]
    public void The_overview_reads_info_version_compose_and_disk_usage()
    {
        var o = DockerSystemOverview.Parse(Info, Version, "2.29.7\n", DiskUsage, []);

        Assert.Equal(("27.3.1", "1.47", "2.29.7"), (o.ServerVersion, o.ApiVersion, o.ComposeVersion));
        Assert.Equal(("overlay2", 8, 33554432000L), (o.StorageDriver, o.Cpus, o.MemoryBytes));
        Assert.Equal(new[] { "io.containerd.runc.v2", "runc" }, o.Runtimes);
        Assert.Equal(new[] { "apparmor", "seccomp", "cgroupns" }, o.SecurityOptions);
        Assert.False(o.Rootless);
        Assert.Equal((12, 9, 3), (o.Containers, o.ContainersRunning, o.ContainersStopped));
        Assert.Equal("WARNING: No swap limit support", Assert.Single(o.Warnings));
        Assert.Equal(4, o.DiskUsage.Count);
        Assert.Equal(("Build Cache", "800MB"), (o.DiskUsage[3].Type, o.DiskUsage[3].Reclaimable));
    }

    [Fact]
    public void Broken_answers_give_an_empty_overview_not_an_error()
    {
        var o = DockerSystemOverview.Parse("not json", null, null, "garbage\n", ["docker info: refused"]);

        Assert.Null(o.ServerVersion);
        Assert.Empty(o.DiskUsage);
        Assert.Equal("docker info: refused", Assert.Single(o.Errors));
    }

    [Fact]
    public async Task The_page_shows_the_engine_the_host_the_disk_usage_and_the_warnings()
    {
        var browser = await SignInAsync();

        var html = await (await browser.GetAsync($"/Environments/Detail?id={_site.EnvId}")).Content.ReadAsStringAsync();

        Assert.Contains("27.3.1", html);
        Assert.Contains("(API 1.47)", html);
        Assert.Contains("2.29.7", html);
        Assert.Contains("overlay2", html);
        Assert.Contains("Debian GNU/Linux 12", html);
        Assert.Contains("No swap limit support", html);
        Assert.Contains("2.1GB (40%)", html);
        Assert.Contains("handler=Cleanup", html);
        Assert.Contains("href=\"/Volumes/Prune?EnvId=", html); // volumes are not part of the cleanup
    }

    [Fact]
    public async Task The_cleanup_runs_only_what_was_ticked_and_never_touches_volumes()
    {
        var browser = await SignInAsync();
        var page = await (await browser.GetAsync($"/Environments/Detail?id={_site.EnvId}")).Content.ReadAsStringAsync();
        _site.App.Hosts.Commands.Clear();

        var post = await browser.PostAsync($"/Environments/Detail?id={_site.EnvId}&handler=Cleanup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["id"] = _site.EnvId.ToString(),
            ["containers"] = "true",
            ["buildCache"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("op=", SignIn.Path(post));

        var operations = _site.App.Services.GetService(typeof(MatDock.Core.Operations.OperationRegistry)) as MatDock.Core.Operations.OperationRegistry;
        var opId = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + SignIn.Path(post)).Query)["op"]!;
        await operations!.WaitAsync(opId);

        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" container prune -f", StringComparison.Ordinal));
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" builder prune -f", StringComparison.Ordinal));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" image prune", StringComparison.Ordinal) || c.Contains(" network prune", StringComparison.Ordinal));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains("volume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_sees_the_page_but_cannot_clean_up()
    {
        var browser = await SignInAsync(UserRole.User);

        var html = await (await browser.GetAsync($"/Environments/Detail?id={_site.EnvId}")).Content.ReadAsStringAsync();
        Assert.Contains("27.3.1", html);
        Assert.DoesNotContain("handler=Cleanup", html);

        _site.App.Hosts.Commands.Clear();
        var post = await browser.PostAsync($"/Environments/Detail?id={_site.EnvId}&handler=Cleanup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["id"] = _site.EnvId.ToString(),
            ["containers"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(post)); // refused, no run started
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" prune", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_limited_to_other_environments_does_not_find_this_one()
    {
        var browser = await SignInAsync(UserRole.User, _site.OtherEnvId);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Environments/Detail?id={_site.EnvId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/Environments/Detail?id={_site.OtherEnvId}")).StatusCode);
    }

    [Fact]
    public async Task The_list_links_the_name_to_the_page()
    {
        var html = await (await (await SignInAsync()).GetAsync("/Environments")).Content.ReadAsStringAsync();

        Assert.Contains($"href=\"/Environments/Detail?id={_site.EnvId}\"", html);
    }
}
