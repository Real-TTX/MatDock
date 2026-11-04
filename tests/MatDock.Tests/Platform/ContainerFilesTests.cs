using System.Net;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The file explorer of a container: reached from the container, called by its name, and the name does not get in the way of a new file's.</summary>
public sealed class ContainerFilesTests : IDisposable
{
    private const string Id = "3f2a9c41d7e0b5a61c2e8f4d9b7a6c5e4d3c2b1a0f9e8d7c6b5a4f3e2d1c0b9a";

    private readonly MatDockFactory _app = new();
    private readonly long _envId;

    public ContainerFilesTests() => _envId = _app.AddLocalEnvironment("files-host");

    public void Dispose() => _app.Dispose();

    private async Task<HttpClient> SignInAsync()
    {
        var browser = _app.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_explorer_is_called_by_the_container_name_and_keeps_it()
    {
        var browser = await SignInAsync();
        var url = $"/Containers/Files?envId={_envId}&container={Id}&containerName=plex&path=config";
        var html = await (await browser.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.Contains("<title>Files &#xB7; plex", html);
        Assert.Contains($"container={Id}&amp;containerName=plex", html); // the links on to the editor and the folders

        var response = await browser.PostAsync($"/Containers/Files?handler=NewFile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["envId"] = _envId.ToString(), ["volume"] = string.Empty, ["container"] = Id, ["containerName"] = "plex", ["path"] = "config", ["name"] = "notes.txt",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("containerName=plex", SignIn.Path(response));
        Assert.Contains(_app.Hosts.Commands, c => c.Contains(Id, StringComparison.Ordinal) && c.Contains("config/notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_name_the_short_id_stands_in()
    {
        var browser = await SignInAsync();
        var html = await (await browser.GetAsync($"/Containers/Files?envId={_envId}&container={Id}")).Content.ReadAsStringAsync();
        Assert.Contains("<title>Files &#xB7; " + Id[..12] + " ", html);
    }
}
