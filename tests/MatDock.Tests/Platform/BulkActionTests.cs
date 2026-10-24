using System.Net;
using System.Text.Json;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Web.Support;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Several rows at once: a tick per row, a bar with the actions, one POST that runs the action on each ticked row and says how it went.</summary>
public sealed class BulkActionTests : IClassFixture<BulkActionTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("bulk-host");
            App.Hosts.Containers.AddRange(
            [
                Container("webid0000001", "web", "running", "nginx:1.27"),
                Container("dbid00000002", "db", "running", "postgres:17"),
            ]);
            App.Connection.Images =
            [
                new DockerImage { Id = "sha256:aaa", Repository = "nginx", Tag = "1.27" },
                new DockerImage { Id = "sha256:bbb", Repository = "redis", Tag = "7" },
            ];
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();

        private static string Container(string id, string name, string state, string image)
            => JsonSerializer.Serialize(new { ID = id, Names = name, Image = image, State = state, Status = "Up 1 hour", Labels = string.Empty, Ports = string.Empty });
    }

    private readonly Site _site;

    public BulkActionTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(UserRole role)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        if (role == UserRole.Admin)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
            return browser;
        }

        var name = "bulk" + Guid.NewGuid().ToString("N")[..8];
        SignIn.AddUser(_site.App, name, "UserPass123!", role);
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        return browser;
    }

    private async Task<(HttpResponseMessage Post, string Message)> BulkAsync(HttpClient browser, string page, string action, params string[] ids)
    {
        var html = await (await browser.GetAsync(page)).Content.ReadAsStringAsync();
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", SignIn.AntiforgeryToken(html)), new("bulkAction", action) };
        form.AddRange(ids.Select(id => new KeyValuePair<string, string>("ids", id)));
        var post = await browser.PostAsync(page + "?handler=Bulk", new FormUrlEncodedContent(form));
        var next = post.StatusCode == HttpStatusCode.Redirect && !SignIn.Path(post).StartsWith("/Account", StringComparison.Ordinal)
            ? await (await browser.GetAsync(SignIn.Path(post))).Content.ReadAsStringAsync()
            : string.Empty;
        var message = System.Text.RegularExpressions.Regex.Match(next, "data-toast=\"([^\"]*)\"|mat-alert__body\">([^<]*)");
        return (post, WebUtility.HtmlDecode(message.Success ? message.Value : string.Empty));
    }

    [Fact]
    public void A_selection_keeps_only_well_formed_rows()
    {
        var rows = Bulk.Parse(["3|web", "3|web", "x|db", "4", "5|", "6|registry.example.com/app:1"]);
        Assert.Equal([(3L, "web"), (6L, "registry.example.com/app:1")], rows);
        Assert.Equal(Bulk.MaxItems, Bulk.Parse(Enumerable.Range(0, 500).Select(i => $"1|c{i}")).Count);

        Assert.Equal(("Stop: 2 done.", false), Bulk.Summary("Stop", [("a", true, "ok"), ("b", true, "ok")]));
        var (text, failed) = Bulk.Summary("Stop", [("a", true, "ok"), ("b", false, "not running")]);
        Assert.True(failed);
        Assert.Equal("Stop: 1 done, 1 failed (b: not running).", text);
    }

    [Fact]
    public async Task The_container_list_has_a_tick_per_row_and_the_bar()
    {
        var html = await (await (await SignInAsync(UserRole.Admin)).GetAsync("/Containers")).Content.ReadAsStringAsync();

        Assert.Contains("data-selectable", html);
        Assert.Contains("data-bulk-bar", html);
        Assert.Contains($"value=\"{_site.EnvId}|web\" form=\"bulk-containers\"", html);
        Assert.Contains("id=\"bulk-containers\"", html);
        Assert.Contains("value=\"Remove\"", html);

        var user = await (await (await SignInAsync(UserRole.User)).GetAsync("/Containers")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"Stop\"", user);
        Assert.DoesNotContain("name=\"bulkAction\" value=\"Remove\"", user);

        var viewer = await (await (await SignInAsync(UserRole.Viewer)).GetAsync("/Containers")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-select-row", viewer);
    }

    [Fact]
    public async Task A_bulk_stop_reaches_every_ticked_container()
    {
        var browser = await SignInAsync(UserRole.User);
        _site.App.Hosts.Commands.Clear();

        var (post, message) = await BulkAsync(browser, "/Containers", "Stop", $"{_site.EnvId}|web", $"{_site.EnvId}|db", "garbage");

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" stop 'web'", StringComparison.Ordinal));
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" stop 'db'", StringComparison.Ordinal));
        Assert.Contains("Stop: 2 done.", message);
    }

    [Fact]
    public async Task Only_an_admin_removes_and_a_viewer_does_nothing()
    {
        var user = await SignInAsync(UserRole.User);
        _site.App.Hosts.Commands.Clear();
        var (refused, _) = await BulkAsync(user, "/Containers", "Remove", $"{_site.EnvId}|web");
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(refused));

        var viewer = await SignInAsync(UserRole.Viewer);
        var (denied, _) = await BulkAsync(viewer, "/Containers", "Stop", $"{_site.EnvId}|web");
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(denied));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" rm ", StringComparison.Ordinal) || c.Contains(" stop ", StringComparison.Ordinal));

        var admin = await SignInAsync(UserRole.Admin);
        var (_, message) = await BulkAsync(admin, "/Containers", "Remove", $"{_site.EnvId}|web");
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" rm -v 'web'", StringComparison.Ordinal));
        Assert.Contains("Remove: 1 done.", message);
    }

    [Fact]
    public async Task An_unknown_action_is_refused()
    {
        var browser = await SignInAsync(UserRole.Admin);
        _site.App.Hosts.Commands.Clear();
        var (_, message) = await BulkAsync(browser, "/Containers", "Kill", $"{_site.EnvId}|web");
        Assert.Contains("Unknown action.", message);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" kill ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Images_networks_and_volumes_are_deleted_in_one_go()
    {
        var browser = await SignInAsync(UserRole.Admin);
        var images = await (await browser.GetAsync("/Images")).Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{_site.EnvId}|redis:7\" form=\"bulk-images\"", images);

        _site.App.Connection.Removed.Clear();
        var (_, imageMessage) = await BulkAsync(browser, "/Images", "Remove", $"{_site.EnvId}|redis:7");
        var (_, networkMessage) = await BulkAsync(browser, "/Networks", "Remove", $"{_site.EnvId}|shop_default", $"{_site.EnvId}|old_net");
        var (_, volumeMessage) = await BulkAsync(browser, "/Volumes", "Remove", $"{_site.EnvId}|shop_data");

        Assert.Equal(["image:redis:7", "network:shop_default", "network:old_net", "volume:shop_data"], _site.App.Connection.Removed);
        Assert.Contains("Delete: 1 done.", imageMessage);
        Assert.Contains("Delete: 2 done.", networkMessage);
        Assert.Contains("Delete: 1 done.", volumeMessage);

        var user = await SignInAsync(UserRole.User);
        var (refused, _) = await BulkAsync(user, "/Volumes", "Remove", $"{_site.EnvId}|shop_data");
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(refused));
    }
}
