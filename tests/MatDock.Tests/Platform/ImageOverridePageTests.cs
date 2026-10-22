using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatDock.Core.Stacks;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Choosing another image than the compose file names: through the API and through the Updates page. The compose file is never changed;
/// the page shows both images. (The image host is a closed local port, so the page's registry check ends at once without leaving the machine.)</summary>
public sealed class ImageOverridePageTests : IClassFixture<ImageOverridePageTests.Site>
{
    private const string Compose = "services:\n  web:\n    image: localhost:1/app:latest\n  db:\n    image: localhost:1/db:16\n";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("override-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new
            {
                ID = "webid0000001",
                Names = "shop-web-1",
                Image = "localhost:1/app:latest",
                State = "running",
                Status = "Up 3 hours",
                Labels = "com.docker.compose.project=shop,com.docker.compose.service=web",
                Ports = string.Empty,
            }));
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public ImageOverridePageTests(Site site) => _site = site;

    private HttpClient Api() => _site.App.Api(MatDockFactory.EngineKey);

    private async Task CreateStackAsync(string name)
    {
        var put = await Api().PutAsJsonAsync($"/api/v1/stacks/{name}", new { composeYaml = Compose });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private Task<HttpResponseMessage> SetAsync(string stack, string service, string? image)
        => Api().PutAsJsonAsync($"/api/v1/stacks/{stack}/images/{service}", new { image });

    private async Task<JsonElement> DetailAsync(string stack)
        => JsonDocument.Parse(await Api().GetStringAsync($"/api/v1/stacks/{stack}")).RootElement.Clone();

    // ---- API ----

    [Fact]
    public async Task The_api_sets_and_clears_an_override_and_the_compose_file_stays()
    {
        await CreateStackAsync("api-stack");

        var set = await SetAsync("api-stack", "web", "localhost:1/app:1.27.3");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var detail = await DetailAsync("api-stack");
        Assert.Equal(Compose, detail.GetProperty("composeYaml").GetString());
        var entry = Assert.Single(detail.GetProperty("imageOverrides").EnumerateArray());
        Assert.Equal("web", entry.GetProperty("service").GetString());
        Assert.Equal("localhost:1/app:latest", entry.GetProperty("base").GetString());
        Assert.Equal("localhost:1/app:1.27.3", entry.GetProperty("image").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SetAsync("api-stack", "web", null)).StatusCode);
        Assert.Empty((await DetailAsync("api-stack")).GetProperty("imageOverrides").EnumerateArray());
    }

    [Theory]
    [InlineData("web", "bad image;id")]
    [InlineData("nope", "nginx:1")]
    [InlineData("bad name", "nginx:1")]
    public async Task The_api_refuses_a_bad_service_or_image(string service, string image)
    {
        await CreateStackAsync("refuse-stack");

        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync("refuse-stack", service, image)).StatusCode);
        Assert.Empty((await DetailAsync("refuse-stack")).GetProperty("imageOverrides").EnumerateArray());
    }

    [Fact]
    public async Task The_api_does_not_know_a_stack_it_does_not_manage()
        => Assert.Equal(HttpStatusCode.NotFound, (await SetAsync("ghost", "web", "nginx:1")).StatusCode);

    // ---- the page ----

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_updates_page_sets_an_override_and_shows_both_images()
    {
        await CreateStackAsync("shop");
        var id = _site.App.Db(db => db.Stacks.Single(s => s.Name == "shop").Id);
        var browser = await AdminAsync();

        var page = await (await browser.GetAsync($"/Stacks/Update?id={id}")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"Image of web\"", page); // a field for the service that runs
        Assert.DoesNotContain("Compose: ", page);

        var post = await browser.PostAsync($"/Stacks/Update?id={id}&handler=Image", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["id"] = id.ToString(),
            ["service"] = "web",
            ["image"] = "localhost:1/app:1.27.3",
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var stack = _site.App.Db(db => db.Stacks.Single(s => s.Id == id));
        Assert.Equal("localhost:1/app:1.27.3", ImageOverrides.Of(stack)["web"].Image);
        Assert.Equal(Compose, stack.ComposeYaml);

        var after = await (await browser.GetAsync(SignIn.Path(post))).Content.ReadAsStringAsync();
        Assert.Contains("Compose: localhost:1/app:latest", after); // what the file says, beside what runs
        Assert.Contains("Override", after);
        Assert.Contains("value=\"localhost:1/app:1.27.3\"", after);

        // The button next to the field takes it back to the compose file's image, whatever the field says.
        Assert.Contains("name=\"reset\"", after);
        var reset = await browser.PostAsync($"/Stacks/Update?id={id}&handler=Image", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(after),
            ["id"] = id.ToString(),
            ["service"] = "web",
            ["image"] = "localhost:1/app:1.27.3",
            ["reset"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Empty(ImageOverrides.Of(_site.App.Db(db => db.Stacks.Single(s => s.Id == id))));

        // Set again: the editor says so, and shows the file as it is.
        var again = await browser.PostAsync($"/Stacks/Update?id={id}&handler=Image", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(after),
            ["id"] = id.ToString(),
            ["service"] = "web",
            ["image"] = "localhost:1/app:1.27.3",
        }));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        var editor = await (await browser.GetAsync($"/Stacks/Edit?id={id}")).Content.ReadAsStringAsync();
        Assert.Contains("MatDock runs other images than this file names", editor);
        Assert.Contains("localhost:1/app:latest", editor);
    }
}
