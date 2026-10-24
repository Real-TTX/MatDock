using System.Net;
using MatDock.Core.Docker;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The events of the engines: the command keeps its filters out of the shell, the parser reads every type, the page filters and links.</summary>
public sealed class EventsPageTests : IClassFixture<EventsPageTests.Site>
{
    private const string Output = """
        {"Type":"container","Action":"start","Actor":{"ID":"runnerid0001aaaa","Attributes":{"name":"runner","image":"nginx:1.27"}},"time":1761300000,"timeNano":1761300000000000000}
        {"Type":"container","Action":"die","Actor":{"ID":"runnerid0001aaaa","Attributes":{"name":"runner","image":"nginx:1.27","exitCode":"137"}},"time":1761300060,"timeNano":1761300060000000000}
        {"Type":"container","Action":"health_status: unhealthy","Actor":{"ID":"dbid00000001bbbb","Attributes":{"name":"db","image":"postgres:17"}},"time":1761300090,"timeNano":1761300090000000000}
        {"Type":"image","Action":"pull","Actor":{"ID":"redis:7","Attributes":{"name":"redis"}},"time":1761300120,"timeNano":1761300120000000000}
        {"Type":"network","Action":"connect","Actor":{"ID":"netid0000001","Attributes":{"name":"shop_default","container":"runnerid0001aaaa","type":"bridge"}},"time":1761300180,"timeNano":1761300180000000000}
        {"Type":"volume","Action":"create","Actor":{"ID":"shop_data","Attributes":{"driver":"local"}},"time":1761300240,"timeNano":1761300240000000000}
        not json
        """;

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("events-host");
            App.Hosts.Respond = command => command.Contains(" events --since ", StringComparison.Ordinal) ? Output : null;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public EventsPageTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    [Fact]
    public void The_command_takes_only_known_filters()
    {
        var command = DockerEvents.Command("docker", 24, "container", "die", "runner");
        Assert.Contains(" events --since 24h --until 0s --filter type=container --filter event=die --filter container='runner' --format '{{json .}}'", command);
        Assert.EndsWith("| tail -n 1000", command);

        var hostile = DockerEvents.Command("docker", 9999, "container; id", "die; id", "x'; id");
        Assert.Contains("--since 168h", hostile);
        Assert.DoesNotContain("--filter", hostile);
    }

    [Fact]
    public void The_parser_reads_every_type_newest_first()
    {
        var events = DockerEvents.Parse(Output);

        Assert.Equal(6, events.Count);
        Assert.Equal(["volume", "network", "image", "container", "container", "container"], events.Select(e => e.Type));

        var volume = events[0];
        Assert.Equal("shop_data", volume.Name);
        var network = events[1];
        Assert.Equal(("shop_default", "runnerid0001aaaa"), (network.Name, network.Container));

        var unhealthy = events.Single(e => e.Action == "health_status");
        Assert.True(unhealthy.IsProblem);
        Assert.Equal("health: unhealthy", unhealthy.Label);

        var died = events.Single(e => e.Action == "die");
        Assert.True(died.IsProblem);
        Assert.Equal("137", died.ExitCode);
        Assert.False(events.Single(e => e.Action == "start").IsProblem);

        Assert.Equal(new DateTime(2025, 10, 24, 10, 0, 0, DateTimeKind.Utc), events[^1].TimeUtc);
        Assert.Single(DockerEvents.Matching(events, "postgres"));
    }

    [Fact]
    public async Task The_page_lists_the_events_and_links_to_the_objects()
    {
        var html = await (await (await SignInAsync()).GetAsync($"/Events?EnvId={_site.EnvId}")).Content.ReadAsStringAsync();

        Assert.Contains("6 events", html);
        Assert.Contains($"/Containers/ContainerDetail?envId={_site.EnvId}&amp;id=runnerid0001aaaa", html);
        Assert.Contains($"/Networks/Detail?envId={_site.EnvId}&amp;network=shop_default", html);
        Assert.Contains($"/Volumes/Detail?envId={_site.EnvId}&amp;volume=shop_data", html);
        Assert.Contains("exit 137", html);
        Assert.Contains("health: unhealthy", html);
    }

    [Fact]
    public async Task The_filters_narrow_the_list_and_reach_docker()
    {
        var browser = await SignInAsync();

        var problems = await (await browser.GetAsync($"/Events?EnvId={_site.EnvId}&Problems=true")).Content.ReadAsStringAsync();
        Assert.Contains("2 events", problems);

        var search = await (await browser.GetAsync($"/Events?EnvId={_site.EnvId}&Q=redis")).Content.ReadAsStringAsync();
        Assert.Contains("1 event<", search);

        _site.App.Hosts.Commands.Clear();
        await browser.GetAsync($"/Events?EnvId={_site.EnvId}&Type=image&Hours=6&Container=runner");
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains("--since 6h --until 0s --filter type=image --filter container='runner'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_menu_leads_to_the_events()
    {
        var browser = await SignInAsync();
        var dashboard = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/Events\"", dashboard);
    }
}
