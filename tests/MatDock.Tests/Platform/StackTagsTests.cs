using System.Net;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Stacks;
using MatDock.Web.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Tags (catalog, stacks, container labels, the list filter) and the icon of a stack chosen in MatDock.</summary>
public sealed class StackTagsTests : IClassFixture<StackTagsTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("tags-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new
            {
                ID = "lab000000001",
                Names = "media-jelly-1",
                Image = "jellyfin/jellyfin:10",
                State = "running",
                Status = "Up 2 hours",
                Labels = "com.docker.compose.project=media,com.docker.compose.service=jelly,matdock.tags=home,media:#ff0000",
                Ports = string.Empty,
            }));
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public StackTagsTests(Site site) => _site = site;

    private async Task<(HttpClient Browser, string Html)> AdminAsync(string page)
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        return (browser, await (await browser.GetAsync(page)).Content.ReadAsStringAsync());
    }

    [Fact]
    public void Tag_lists_are_clean_and_labels_keep_their_commas()
    {
        Assert.Equal(new[] { "prod", "web", "eu" }, TagService.Parse(" prod, web ,Prod,, eu:#00ff00 "));
        Assert.Equal(TagService.MaxPerStack, TagService.Parse(string.Join(",", Enumerable.Range(0, 30).Select(i => $"t{i}"))).Count);
        Assert.Empty(TagService.Parse(new string('x', 33)));
        Assert.Null(TagService.Join([]));

        var labels = ContainerService.ParseLabels("a=1,matdock.tags=home,media:#ff0000,traefik.http.routers.x.rule=Host(`a.example`,`b.example`),z=2");
        Assert.Equal("home,media:#ff0000", labels["matdock.tags"]);
        Assert.Equal("Host(`a.example`,`b.example`)", labels["traefik.http.routers.x.rule"]);
        Assert.Equal(new[] { "home", "media" }, TagService.FromLabels(labels));
        Assert.Equal(new[] { "x" }, TagService.FromLabels(new Dictionary<string, string> { ["dockhand.tags"] = "x" }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("database", true)]
    [InlineData("🐳", true)]
    [InlineData("https://example.org/logo.png", true)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", true)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("<svg onload=x>", false)]
    public void An_icon_is_a_name_a_glyph_an_https_address_or_an_image(string? icon, bool valid)
        => Assert.Equal(valid, StackIcons.Problem(icon) is null);

    [Theory]
    [InlineData("Uptime Kuma", "uptime-kuma")]
    [InlineData("nextcloud", "nextcloud")]
    [InlineData("../etc", "etc")]
    [InlineData("!!!", null)]
    public void An_app_name_becomes_the_collections_name(string name, string? slug)
        => Assert.Equal(slug, AppLogos.Slug(name));

    [Fact]
    public async Task The_editor_saves_tags_and_icon_and_new_tags_join_the_catalog()
    {
        var (browser, html) = await AdminAsync("/Stacks/Edit");
        var response = await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.Name"] = "tagged",
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Source"] = "inline",
            ["Input.ComposeYaml"] = "services:\n  web:\n    image: nginx:1.27\n",
            ["Input.Tags"] = "production, web",
            ["Input.Icon"] = "database",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var stack = _site.App.Db(db => db.Stacks.Single(s => s.Name == "tagged"));
        Assert.Equal("production, web", stack.Tags);
        Assert.Equal("database", stack.Icon);
        Assert.True(_site.App.Db(db => db.Tags.Any(t => t.Name == "production")));

        var bad = await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.Name"] = "bad-icon",
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Source"] = "inline",
            ["Input.ComposeYaml"] = "services:\n  web:\n    image: nginx:1.27\n",
            ["Input.Icon"] = "data:text/html;base64,PHNjcmlwdD4=",
        }));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.False(_site.App.Db(db => db.Stacks.Any(s => s.Name == "bad-icon")));

        // The list shows the icon and the tags and filters by a tag.
        var list = await (await browser.GetAsync("/Stacks?view=list&Tag=production")).Content.ReadAsStringAsync();
        Assert.Contains("tagged", list);
        Assert.Contains("Tag=production", list);
        var other = await (await browser.GetAsync("/Stacks?view=list&Tag=nope")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("href=\"/Stacks/Edit?id=" + stack.Id + "\"", other);

        var gallery = await (await browser.GetAsync("/Stacks?view=gallery&Tag=web")).Content.ReadAsStringAsync();
        Assert.Contains("#mat-i-database", gallery);
    }

    [Fact]
    public async Task A_running_project_shows_the_tags_of_its_labels()
    {
        var (browser, _) = await AdminAsync("/");
        var list = await (await browser.GetAsync("/Stacks?view=list&Tag=media")).Content.ReadAsStringAsync();
        Assert.Contains("media", list);
        Assert.Contains("Tag=home", list);
    }

    [Fact]
    public async Task Renaming_and_deleting_a_tag_follow_on_the_stacks()
    {
        using (var scope = _site.App.Services.CreateScope())
        {
            var stacks = scope.ServiceProvider.GetRequiredService<StackService>();
            await stacks.CreateAsync(new StackInput { Name = "rename-a", EnvironmentId = _site.EnvId, ComposeYaml = "services: {}\n", Tags = "old, keep" });
            await stacks.CreateAsync(new StackInput { Name = "rename-b", EnvironmentId = _site.EnvId, ComposeYaml = "services: {}\n", Tags = "old" });
            await scope.ServiceProvider.GetRequiredService<TagService>().EnsureAsync(["old", "keep"]);
        }

        var (browser, html) = await AdminAsync("/Settings/Tags");
        Assert.Contains("value=\"old\"", html);
        var oldId = _site.App.Db(db => db.Tags.Single(t => t.Name == "old").Id);
        var rename = await browser.PostAsync("/Settings/Tags?handler=Rename", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["id"] = oldId.ToString(),
            ["name"] = "new",
        }));
        Assert.Equal(HttpStatusCode.Redirect, rename.StatusCode);
        Assert.Equal("new, keep", _site.App.Db(db => db.Stacks.Single(s => s.Name == "rename-a").Tags));
        Assert.Equal("new", _site.App.Db(db => db.Stacks.Single(s => s.Name == "rename-b").Tags));

        var colour = await browser.PostAsync("/Settings/Tags?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["name"] = "new",
            ["color"] = "#12AB34",
        }));
        Assert.Equal(HttpStatusCode.Redirect, colour.StatusCode);
        Assert.Equal("#12ab34", _site.App.Db(db => db.Tags.Single(t => t.Name == "new").Color));

        var delete = await browser.PostAsync("/Settings/Tags?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["id"] = oldId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Equal("keep", _site.App.Db(db => db.Stacks.Single(s => s.Name == "rename-a").Tags));
        Assert.Null(_site.App.Db(db => db.Stacks.Single(s => s.Name == "rename-b").Tags));
    }

    [Fact]
    public async Task An_unknown_logo_answers_not_found_without_asking_the_network()
    {
        var (browser, html) = await AdminAsync("/Stacks/Edit");
        var response = await browser.PostAsync("/Stacks/Edit?handler=Logo", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["name"] = "!!!",
        }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
