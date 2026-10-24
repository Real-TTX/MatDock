using System.Net;
using System.Text.Json;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using MatDock.Web.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The search of the header bar: what the hosts hold, the environments and the pages, by group; only what the user may see; asked once per window.</summary>
public sealed class GlobalSearchTests : IClassFixture<GlobalSearchTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("search-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new
            {
                ID = "webid0000001", Names = "shop-web-1", Image = "nginx:1.27", State = "running", Status = "Up 1 hour",
                Labels = "com.docker.compose.project=shop,com.docker.compose.service=web", Ports = string.Empty,
            }));
            App.Connection.Images = [new DockerImage { Id = "sha256:aaa", Repository = "nginx", Tag = "1.27", Size = "190MB" }];
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public GlobalSearchTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(UserRole role, string? name = null)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        if (role == UserRole.Admin && name is null)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
            return browser;
        }

        name ??= "search" + Guid.NewGuid().ToString("N")[..8];
        if (_site.App.Db(db => !db.Users.Any(u => u.Username == name)))
        {
            SignIn.AddUser(_site.App, name, "UserPass123!", role);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        return browser;
    }

    private static async Task<Dictionary<string, List<(string Title, string Href)>>> SuggestAsync(HttpClient browser, string q, int limit = 8)
    {
        var response = await browser.GetAsync($"/api/search?q={Uri.EscapeDataString(q)}&limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("groups").EnumerateArray().ToDictionary(
            g => g.GetProperty("name").GetString()!,
            g => g.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("title").GetString()!, i.GetProperty("href").GetString()!)).ToList());
    }

    [Fact]
    public void The_limit_is_shared_among_the_groups()
    {
        SearchItem Item(string t) => new(t, null, "/" + t, "box", "x");
        var groups = new List<SearchGroup>
        {
            new("A", Enumerable.Range(0, 10).Select(i => Item("a" + i)).ToList()),
            new("B", [Item("b0")]),
            new("C", [Item("c0"), Item("c1")]),
        };

        var shared = SearchEndpoint.Share(groups, 5);

        Assert.Equal([2, 1, 2], shared.Select(g => g.Items.Count));
        Assert.Equal(13, SearchEndpoint.Share(groups, 50).Sum(g => g.Items.Count));
    }

    [Fact]
    public async Task A_container_its_stack_and_its_image_are_found()
    {
        var groups = await SuggestAsync(await SignInAsync(UserRole.Admin), "shop");

        var container = Assert.Single(groups["Containers"]);
        Assert.Equal(("shop-web-1", $"/Containers/ContainerDetail?envId={_site.EnvId}&id=webid0000001"), container);
        Assert.Equal(("shop", $"/Containers/Stack?envId={_site.EnvId}&project=shop"), Assert.Single(groups["Stacks"]));

        var image = await SuggestAsync(await SignInAsync(UserRole.Admin), "nginx");
        Assert.Contains(("nginx:1.27", $"/Images/Detail?envId={_site.EnvId}&reference=nginx%3A1.27"), image["Images"]);
        Assert.Contains(image["Containers"], c => c.Title == "shop-web-1");
    }

    [Fact]
    public async Task Pages_and_environments_are_found_admin_pages_only_by_admins()
    {
        var admin = await SuggestAsync(await SignInAsync(UserRole.Admin), "sched");
        Assert.Contains(("Schedules", "/Schedules"), admin["Pages"]);

        var user = await SuggestAsync(await SignInAsync(UserRole.User), "sched");
        Assert.False(user.ContainsKey("Pages"));

        var env = await SuggestAsync(await SignInAsync(UserRole.Viewer), "search-h");
        Assert.Equal(("search-host", $"/Environments/Detail?id={_site.EnvId}"), Assert.Single(env["Environments"]));
    }

    [Fact]
    public async Task A_user_limited_to_no_environment_finds_nothing_of_the_hosts()
    {
        var name = "nowhere" + Guid.NewGuid().ToString("N")[..6];
        var id = SignIn.AddUser(_site.App, name, "UserPass123!", UserRole.User);
        using (var scope = _site.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserService>().SetEnvironmentAccessAsync(id, true, []);
        }

        var groups = await SuggestAsync(await SignInAsync(UserRole.User, name), "shop");
        Assert.Empty(groups);
    }

    [Fact]
    public async Task The_hosts_are_asked_once_per_window()
    {
        var browser = await SignInAsync(UserRole.Admin);
        await SuggestAsync(browser, "sh");
        _site.App.Hosts.Commands.Clear();

        await SuggestAsync(browser, "sho");
        await SuggestAsync(browser, "shop");

        Assert.Empty(_site.App.Hosts.Commands);
    }

    [Fact]
    public async Task Without_a_sign_in_there_are_no_suggestions()
    {
        var response = await _site.App.Browser().GetAsync("/api/search?q=shop");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_header_has_the_field_and_the_results_page_lists_every_group()
    {
        var browser = await SignInAsync(UserRole.Admin);
        var dashboard = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("data-search-url=\"/api/search\"", dashboard);
        Assert.Contains("data-results-url=\"/Search\"", dashboard);

        var results = await (await browser.GetAsync("/Search?q=shop")).Content.ReadAsStringAsync();
        Assert.Contains("shop-web-1", results);
        Assert.Contains("id=\"results-containers\"", results);
        Assert.Contains("id=\"results-stacks\"", results);

        var none = await (await browser.GetAsync("/Search?q=zzzz-nothing")).Content.ReadAsStringAsync();
        Assert.Contains("No results for", none);
    }
}
