using System.Net;
using MatDock.Core.Entities;
using MatDock.Web.Support;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The search and sort of the short lists, the About page behind the version of the menu, and what an installed app needs besides it.</summary>
public sealed class ListsAndAboutTests : IClassFixture<ListsAndAboutTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            App.Db(db =>
            {
                db.GitRepos.AddRange(
                    new GitRepo { Name = "alpha-repo", Url = "https://git.example.com/team/alpha.git" },
                    new GitRepo { Name = "beta-repo", Url = "https://git.example.com/team/beta.git" },
                    new GitRepo { Name = "gamma-repo", Url = "https://git.example.com/other/gamma.git" });
                db.ContainerRegistries.AddRange(
                    new ContainerRegistry { Name = "hub", Host = "registry.example.com", Username = "zed" },
                    new ContainerRegistry { Name = "mirror", Host = "mirror.example.com", Username = "amy" });
                db.ProxyConnections.AddRange(
                    new ProxyConnection { Name = "edge", Url = "http://edge.example.com:2019" },
                    new ProxyConnection { Name = "inner", Url = "http://inner.example.com:2019" });
                db.SaveChanges();
                return 0;
            });
        }

        public MatDockFactory App { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public ListsAndAboutTests(Site site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    private static List<string> Positions(string html, params string[] names)
        => names.Where(html.Contains).OrderBy(n => html.IndexOf(n, StringComparison.Ordinal)).ToList();

    // ---- the lists have a toolbar that searches and sorts ----

    [Theory]
    [InlineData("/GitRepos")]
    [InlineData("/Settings/Registries")]
    [InlineData("/Settings/Proxies")]
    [InlineData("/GitCredentials")]
    [InlineData("/Stacks/SyncJobs")]
    [InlineData("/Updates")]
    public async Task These_lists_have_a_toolbar_with_search_and_sort_above_the_table(string path)
    {
        var html = await (await (await AdminAsync()).GetAsync(path)).Content.ReadAsStringAsync();

        Assert.Contains("name=\"Q\"", html);
        Assert.Contains("name=\"SortKey\"", html);
        Assert.True(html.IndexOf("name=\"Q\"", StringComparison.Ordinal) < html.IndexOf("mat-table-wrap", StringComparison.Ordinal) || !html.Contains("mat-table-wrap"));
    }

    [Fact]
    public async Task A_search_narrows_the_list_and_a_search_without_a_hit_says_so()
    {
        var browser = await AdminAsync();

        var narrowed = await (await browser.GetAsync("/GitRepos?Q=other")).Content.ReadAsStringAsync();
        Assert.Contains("gamma-repo", narrowed);
        Assert.DoesNotContain("alpha-repo", narrowed); // found in the URL, not the name

        var none = await (await browser.GetAsync("/GitRepos?Q=nothing-like-this")).Content.ReadAsStringAsync();
        Assert.Contains("No git repositories found.", none); // not "yet": there are some
        Assert.DoesNotContain("alpha-repo", none);
    }

    [Fact]
    public async Task The_sort_orders_the_list_and_an_unknown_sort_falls_back_to_the_first()
    {
        var browser = await AdminAsync();

        var up = await (await browser.GetAsync("/Settings/Registries?SortKey=UserAsc")).Content.ReadAsStringAsync();
        Assert.Equal(new[] { "mirror", "hub" }, Positions(up, "mirror.example.com", "registry.example.com").Select(h => h.StartsWith("mirror") ? "mirror" : "hub")); // amy before zed

        var down = await (await browser.GetAsync("/Settings/Registries?SortKey=UserDesc")).Content.ReadAsStringAsync();
        Assert.Equal(new[] { "hub", "mirror" }, Positions(down, "mirror.example.com", "registry.example.com").Select(h => h.StartsWith("mirror") ? "mirror" : "hub"));

        var odd = await browser.GetAsync("/Settings/Registries?SortKey=DropTable");
        Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
    }

    [Fact]
    public void The_shared_list_logic_searches_case_blind_in_all_fields_and_sorts_both_ways()
    {
        var sorts = ListView.Sorts<Item>(("Name", i => i.Name), ("Size", i => i.Size));
        var items = new[] { new Item("Beta", 2), new Item("alpha", 30), new Item("Gamma", 10) };

        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, ListView.Apply(items, null, sorts, "NameAsc", i => i.Name).Select(i => i.Name));
        Assert.Equal(new[] { "Gamma", "Beta", "alpha" }, ListView.Apply(items, null, sorts, "NameDesc", i => i.Name).Select(i => i.Name));
        Assert.Equal(new[] { "Beta", "Gamma", "alpha" }, ListView.Apply(items, null, sorts, "SizeAsc", i => i.Name).Select(i => i.Name)); // numbers as numbers
        Assert.Equal(new[] { "alpha" }, ListView.Apply(items, "  ALP ", sorts, null, i => i.Name).Select(i => i.Name));
        Assert.Equal(new[] { "Gamma" }, ListView.Apply(items, "10", sorts, null, i => i.Name, i => i.Size.ToString()).Select(i => i.Name));
        Assert.Equal("NameAsc", ListView.Toolbar<Item>(null, "nonsense", "Search", sorts).SortKey);
    }

    private sealed record Item(string Name, int Size);

    // ---- About ----

    [Fact]
    public async Task The_version_at_the_bottom_of_the_menu_leads_to_the_about_page_which_names_the_parts()
    {
        var browser = await AdminAsync();

        var home = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Matches("<a class=\"mat-nav__version[^\"]*\" href=\"/About\"", home);

        var about = await (await browser.GetAsync("/About")).Content.ReadAsStringAsync();
        Assert.Contains(MatDock.Core.AppVersion.Informational, about);
        Assert.Contains("MatUi", about);
        Assert.Contains("MatPlatform", about);
        Assert.Matches("class=\"mat-pill[^\"]*\">(Release|Nightly|Local)</span>", about); // the channel, from the shape of the version
        Assert.Matches(@"MatUi</dt><dd class=""mat-mono"">\d+\.\d+\.\d+</dd>", about); // read from the toolkit copy, not "unknown"
    }

    [Fact]
    public async Task The_about_page_needs_a_sign_in()
    {
        var response = await _site.App.Browser().GetAsync("/About");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", SignIn.Path(response));
    }

    // ---- crawlers and the installed app ----

    [Fact]
    public async Task Crawlers_are_kept_out_without_a_sign_in()
    {
        var response = await _site.App.Browser().GetAsync("/robots.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("User-agent: *", text);
        Assert.Contains("Disallow: /", text);
    }

    [Fact]
    public async Task The_offline_page_looks_for_the_server_by_itself_and_the_worker_keeps_it()
    {
        var client = _site.App.Browser();

        var page = await (await client.GetAsync("/offline.html")).Content.ReadAsStringAsync();
        Assert.Contains("/js/offline.js", page);
        Assert.Contains("id=\"offline-status\"", page);
        Assert.Contains("Try again", page); // the manual way stays

        var script = await (await client.GetAsync("/js/offline.js")).Content.ReadAsStringAsync();
        Assert.Contains("/api/health", script);
        Assert.Contains("\"online\"", script);

        var worker = await (await client.GetAsync("/sw.js")).Content.ReadAsStringAsync();
        Assert.Contains("\"/js/offline.js\"", worker);
        Assert.Contains("\"/img/icon-maskable-512.png\"", worker);
    }

    [Fact]
    public async Task The_maskable_icon_is_a_file_of_its_own_with_room_around_the_picture()
    {
        var client = _site.App.Browser();
        var manifest = await (await client.GetAsync("/manifest.webmanifest")).Content.ReadAsStringAsync();

        Assert.Contains("\"src\": \"/img/icon-maskable-512.png\", \"sizes\": \"512x512\", \"type\": \"image/png\", \"purpose\": \"maskable\"", manifest);
        var icon = await client.GetAsync("/img/icon-maskable-512.png");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_page_tells_when_the_connection_is_lost_and_reads_again_when_it_is_back()
    {
        var script = await (await _site.App.Browser().GetAsync("/js/matdock.js")).Content.ReadAsStringAsync();

        Assert.Contains("addEventListener(\"offline\"", script);
        Assert.Contains("addEventListener(\"online\", refresh)", script); // the live regions read again at once
        Assert.Contains("md-offline", script);
    }
}

/// <summary>The files of the repository that say how it is run: the compose files are as simple as the guidelines want (one stack, no variables, no placeholders).</summary>
public class ComposeFilesTests
{
    private static string Read(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatDock.slnx")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, name));
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.dev.yml")]
    public void A_compose_file_has_no_environment_variables_no_container_names_and_one_named_stack(string file)
    {
        var text = Read(file);

        Assert.DoesNotContain("environment:", text);
        Assert.DoesNotContain("container_name:", text); // the name is made of the stack, the service and the number
        Assert.Contains("name: matdock", text);
    }

    [Fact]
    public void The_volume_is_called_data_so_the_stack_does_not_say_matdock_twice()
    {
        var text = Read("docker-compose.yml");

        Assert.Contains("- data:/data", text);
        Assert.DoesNotContain("matdock-data", text);
        Assert.Contains("  web:", text);
    }

    [Fact]
    public void Development_mode_comes_from_the_dev_stack_only_as_a_build_argument()
    {
        Assert.Contains("AspNetEnvironment: Development", Read("docker-compose.dev.yml"));
        Assert.DoesNotContain("AspNetEnvironment", Read("docker-compose.yml"));
        Assert.Contains("ARG AspNetEnvironment=Production", Read("Dockerfile"));
    }
}
