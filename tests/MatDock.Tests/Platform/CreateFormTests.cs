using System.Net;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>When Docker refuses to create a volume or a network, the form says why. (It used to show the form again without a word: the message went
/// into a TempData that the form never renders.)</summary>
public sealed class CreateFormTests : IClassFixture<CreateFormTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("create-host");
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public CreateFormTests(Site site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, params (string Key, string Value)[] fields)
    {
        var form = await (await browser.GetAsync(page)).Content.ReadAsStringAsync();
        var data = fields.ToDictionary(f => f.Key, f => f.Value);
        data["__RequestVerificationToken"] = SignIn.AntiforgeryToken(form);
        data["Input.EnvironmentId"] = _site.EnvId.ToString();
        return await browser.PostAsync(page, new FormUrlEncodedContent(data));
    }

    [Theory]
    [InlineData("/Networks/Create", "/Networks")]
    [InlineData("/Volumes/Create", "/Volumes")]
    public async Task A_refusal_of_docker_is_shown_on_the_form(string page, string list)
    {
        var browser = await AdminAsync();
        _site.App.Connection.CreateResult = (false, "Docker error: it already exists.");
        try
        {
            var response = await PostAsync(browser, page, ("Input.Name", "dup"), ("Input.Type", "simple"), ("Input.Driver", "bridge"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the form again ...
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Docker error: it already exists.", body); // ... with the reason
            Assert.DoesNotContain("data-validation-summary hidden", body);

            // Nothing is left over for the list page to announce as done.
            Assert.DoesNotContain("data-toast", await (await browser.GetAsync(list)).Content.ReadAsStringAsync());
        }
        finally
        {
            _site.App.Connection.CreateResult = (true, "ok");
        }
    }

    [Theory]
    [InlineData("/Networks/Create", "/Networks")]
    [InlineData("/Volumes/Create", "/Volumes")]
    public async Task A_created_one_leads_back_to_the_list_with_a_confirmation(string page, string list)
    {
        var browser = await AdminAsync();
        _site.App.Connection.CreateResult = (true, "Created it.");
        try
        {
            var response = await PostAsync(browser, page, ("Input.Name", "fresh"), ("Input.Type", "simple"), ("Input.Driver", "bridge"));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith(list, SignIn.Path(response));
            Assert.Contains("Created it.", await (await browser.GetAsync(SignIn.Path(response))).Content.ReadAsStringAsync());
        }
        finally
        {
            _site.App.Connection.CreateResult = (true, "ok");
        }
    }
}
