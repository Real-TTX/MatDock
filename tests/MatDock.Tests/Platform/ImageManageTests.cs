using System.Net;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Images;
using MatDock.Core.Registries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>An image: which containers use it, another tag, a push to its registry (signed in first), and a file to carry it to a host without a registry.</summary>
public sealed class ImageManageTests : IClassFixture<ImageManageTests.Site>
{
    private const string Inspect = """{"Id":"sha256:abc","RepoTags":["registry.example.com/team/app:1.2"],"RepoDigests":[],"Created":"2026-10-20T10:00:00Z","Size":1000,"Os":"linux","Architecture":"amd64","Config":{"Labels":{}}}""";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("image-host");
            App.Hosts.Respond = command =>
                command.Contains(" image inspect --format '{{json .}}'", StringComparison.Ordinal) ? Inspect
                : command.Contains(" ps -a --filter ancestor=", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { ID = "appid0000001", Names = "shop-app-1", Image = "registry.example.com/team/app:1.2", State = "running", Status = "Up 1 hour",
                        Labels = "com.docker.compose.project=shop", Ports = string.Empty })
                : command.Contains(" image load", StringComparison.Ordinal) ? "Loaded image: registry.example.com/team/app:1.2\n"
                : null;

            using var scope = App.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<RegistryService>()
                .CreateAsync(new RegistryInput { Name = "team", Host = "registry.example.com", Username = "robot", Token = "s3cret-token" }).GetAwaiter().GetResult();
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public ImageManageTests(Site site) => _site = site;

    private string DetailUrl => $"/Images/Detail?envId={_site.EnvId}&reference={Uri.EscapeDataString("registry.example.com/team/app:1.2")}";

    private async Task<HttpClient> SignInAsync(UserRole role = UserRole.Admin)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        if (role == UserRole.Admin)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
            return browser;
        }

        var name = "img" + Guid.NewGuid().ToString("N")[..8];
        SignIn.AddUser(_site.App, name, "UserPass123!", role);
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        return browser;
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient browser, string handler, params (string Key, string Value)[] fields)
    {
        var page = await (await browser.GetAsync(DetailUrl)).Content.ReadAsStringAsync();
        var data = fields.ToDictionary(f => f.Key, f => f.Value);
        data["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page);
        data["envId"] = _site.EnvId.ToString();
        data["reference"] = "registry.example.com/team/app:1.2";
        return await browser.PostAsync($"{DetailUrl}&handler={handler}", new FormUrlEncodedContent(data));
    }

    [Fact]
    public void The_commands_quote_the_reference()
    {
        Assert.EndsWith("docker ps -a --filter ancestor='nginx:1.27' --format '{{json .}}'", ImageCommands.UsedBy("nginx:1.27"));
        Assert.EndsWith("docker push 'reg.example.com/app:1' 2>&1", ImageCommands.Push("reg.example.com/app:1"));
        Assert.Contains("set -o pipefail", ImageCommands.Save("nginx:1.27"));
        Assert.EndsWith("docker image save 'nginx:1.27' | gzip -c", ImageCommands.Save("nginx:1.27"));
        Assert.EndsWith("docker image load", ImageCommands.Load());
        Assert.Throws<ArgumentException>(() => ImageCommands.Push("x'; id"));
    }

    [Fact]
    public async Task The_page_lists_the_containers_that_use_the_image()
    {
        var html = await (await (await SignInAsync()).GetAsync(DetailUrl)).Content.ReadAsStringAsync();

        Assert.Contains("shop-app-1", html);
        Assert.Contains("/Containers/ContainerDetail?envId=", html);
        Assert.Contains("handler=Tag", html);
        Assert.Contains("Push registry.example.com/team/app:1.2", html);
        Assert.Contains("handler=Save", html);
    }

    [Fact]
    public async Task A_tag_is_added_and_a_bad_one_never_reaches_docker()
    {
        var browser = await SignInAsync();
        _site.App.Hosts.Commands.Clear();

        await PostAsync(browser, "Tag", ("target", "registry.example.com/team/app:stable"));
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" image tag 'registry.example.com/team/app:1.2' 'registry.example.com/team/app:stable'", StringComparison.Ordinal));

        _site.App.Hosts.Commands.Clear();
        await PostAsync(browser, "Tag", ("target", "x'; touch /tmp/p"));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" image tag ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_push_signs_in_to_the_registry_first_without_the_token_on_the_command_line()
    {
        var browser = await SignInAsync();
        _site.App.Hosts.Commands.Clear();

        var post = await PostAsync(browser, "Push", ("tag", "registry.example.com/team/app:1.2"));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var opId = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + SignIn.Path(post)).Query)["op"]!;
        await _site.App.Services.GetRequiredService<MatDock.Core.Operations.OperationRegistry>().WaitAsync(opId);

        var login = _site.App.Hosts.Commands.FindIndex(c => c.Contains(" login 'registry.example.com' -u 'robot' --password-stdin", StringComparison.Ordinal));
        var push = _site.App.Hosts.Commands.FindIndex(c => c.Contains(" push 'registry.example.com/team/app:1.2'", StringComparison.Ordinal));
        Assert.True(login >= 0 && push > login);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains("s3cret-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_answers_a_gzip_file_from_docker_save()
    {
        var browser = await SignInAsync();
        _site.App.Hosts.Commands.Clear();

        var response = await browser.GetAsync($"{DetailUrl}&handler=Save");

        Assert.Equal("application/gzip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("registry.example.com_team_app_1.2.tar.gz", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" image save 'registry.example.com/team/app:1.2' | gzip -c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_streams_the_file_into_docker_load()
    {
        var browser = await SignInAsync();
        var page = await (await browser.GetAsync("/Images/Load")).Content.ReadAsStringAsync();
        _site.App.Hosts.Commands.Clear();

        using var form = new MultipartFormDataContent
        {
            { new StringContent(SignIn.AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent(_site.EnvId.ToString()), "EnvironmentId" },
            { new ByteArrayContent(new byte[] { 0x1f, 0x8b, 0x08, 0x00 }), "file", "app.tar.gz" },
        };
        var post = await browser.PostAsync("/Images/Load", form);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains(_site.App.Hosts.Commands, c => c.EndsWith(" image load", StringComparison.Ordinal));
        var list = await (await browser.GetAsync(SignIn.Path(post))).Content.ReadAsStringAsync();
        Assert.Contains("Loaded image: registry.example.com/team/app:1.2", System.Net.WebUtility.HtmlDecode(list));
    }

    [Theory]
    [InlineData("Tag")]
    [InlineData("Push")]
    public async Task A_user_cannot_tag_or_push(string handler)
    {
        var browser = await SignInAsync(UserRole.User);
        _site.App.Hosts.Commands.Clear();

        var post = await PostAsync(browser, handler, ("target", "a:b"), ("tag", "registry.example.com/team/app:1.2"));

        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(post));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" tag ", StringComparison.Ordinal) || c.Contains(" push ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_cannot_export_or_import()
    {
        var browser = await SignInAsync(UserRole.User);

        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(await browser.GetAsync($"{DetailUrl}&handler=Save")));
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(await browser.GetAsync("/Images/Load")));
    }
}
