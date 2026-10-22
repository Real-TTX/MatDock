using System.Net;
using System.Text.Json;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The buttons of the Containers page really reach Docker: what the page posts (no more, no less) ends up as the docker command.</summary>
public sealed class ContainerActionTests : IClassFixture<ContainerActionTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("actions-host");
            App.Hosts.Containers.AddRange(
            [
                Container("runnerid0001", "running", "Up 3 hours", "runner"),
                Container("pausedid0002", "paused", "Up 3 hours (Paused)", "napper"),
                Container("stoppedid003", "exited", "Exited (0) 2 days ago", "sleeper"),
            ]);
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();

        private static string Container(string id, string state, string status, string name)
            => JsonSerializer.Serialize(new { ID = id, Names = name, Image = "nginx", State = state, Status = status, Labels = string.Empty, Ports = string.Empty });
    }

    private readonly Site _site;

    public ContainerActionTests(Site site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    /// <summary>Posts the form of one button exactly as the page renders it (<c>envId</c>, <c>id</c>, <c>name</c>, <c>action</c>) and returns the message the next page shows.</summary>
    private async Task<(HttpResponseMessage Post, string Message)> ClickAsync(HttpClient browser, string id, string name, string action)
    {
        var page = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();
        var post = await browser.PostAsync("/Containers?handler=Action", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["envId"] = _site.EnvId.ToString(),
            ["id"] = id,
            ["name"] = name,
            ["action"] = action,
        }));
        var next = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();
        var message = System.Text.RegularExpressions.Regex.Match(next, "data-toast=\"([^\"]*)\"|mat-alert__body\">([^<]*)");
        return (post, System.Net.WebUtility.HtmlDecode(message.Success ? message.Value : string.Empty));
    }

    [Theory]
    [InlineData("Start", "start")]
    [InlineData("Stop", "stop")]
    [InlineData("Restart", "restart")]
    [InlineData("Pause", "pause")]
    [InlineData("Unpause", "unpause")]
    [InlineData("Kill", "kill")]
    public async Task A_button_runs_its_docker_command_on_the_container(string action, string verb)
    {
        var browser = await AdminAsync();
        _site.App.Hosts.Commands.Clear();

        var (post, message) = await ClickAsync(browser, "runnerid0001", "runner", action);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.DoesNotContain("Invalid action", message); // the page's own form carries no "view": that must not make it invalid
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains($" {verb} 'runnerid0001'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Teleport")]
    [InlineData("rm -rf /")]
    [InlineData("")]
    public async Task An_unknown_action_is_refused_and_runs_nothing(string action)
    {
        var browser = await AdminAsync();
        _site.App.Hosts.Commands.Clear();

        var (_, message) = await ClickAsync(browser, "runnerid0001", "runner", action);

        Assert.Contains("Invalid action", message);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains("runnerid0001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_container_id_with_shell_characters_never_reaches_docker()
    {
        var browser = await AdminAsync();
        _site.App.Hosts.Commands.Clear();

        var (_, message) = await ClickAsync(browser, "x'; touch /tmp/pwned; '", "evil", "Kill");

        Assert.Contains("Invalid container ID", message);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains("pwned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_page_offers_the_buttons_that_fit_the_state_of_each_container()
    {
        var browser = await AdminAsync();
        var html = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();

        string ActionsOf(string id)
        {
            var flat = html.Replace('\n', ' ');
            var forms = System.Text.RegularExpressions.Regex.Matches(flat, "name=\"id\" value=\"" + id + "\".*?name=\"action\" value=\"([A-Za-z]+)\"");
            return string.Join(",", forms.Select(m => m.Groups[1].Value).Order());
        }

        Assert.Equal("Kill,Pause,Restart,Stop", ActionsOf("runnerid0001")); // running: pause, restart, stop, kill
        Assert.Equal("Stop,Unpause", ActionsOf("pausedid0002"));            // paused: continue it, or stop it
        Assert.Equal("Start", ActionsOf("stoppedid003"));                   // stopped: start
    }
}
