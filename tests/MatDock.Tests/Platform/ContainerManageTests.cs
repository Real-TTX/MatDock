using System.Net;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Removing, renaming and pruning containers, and the detail page that reads <c>docker inspect</c> (secrets masked).</summary>
public sealed class ContainerManageTests : IClassFixture<ContainerManageTests.Site>
{
    public const string InspectJson = """
        [{
          "Id": "webid000000000000000000000000000000000000000000000000000000000001",
          "Name": "/shop-web-1",
          "Created": "2026-10-20T10:00:00.123Z",
          "Image": "sha256:abc",
          "RestartCount": 3,
          "State": { "Status": "running", "Running": true, "Paused": false, "Restarting": false, "OOMKilled": false, "ExitCode": 0,
                     "StartedAt": "2026-10-21T08:30:00Z", "FinishedAt": "0001-01-01T00:00:00Z", "Error": "",
                     "Health": { "Status": "unhealthy", "FailingStreak": 2, "Log": [
                        { "Start": "2026-10-24T10:00:00Z", "End": "2026-10-24T10:00:01Z", "ExitCode": 1, "Output": "curl: (7) Failed to connect\n" } ] } },
          "Config": { "Hostname": "web1", "User": "app", "WorkingDir": "/srv", "Image": "nginx:1.27",
                      "Env": ["TZ=Europe/Berlin", "DB_PASSWORD=hunter2", "API_KEY=abc123", "EMPTY_SECRET="],
                      "Cmd": ["nginx", "-g", "daemon off;"], "Entrypoint": ["/docker-entrypoint.sh"],
                      "Labels": { "com.docker.compose.project": "shop", "com.docker.compose.service": "web" },
                      "Healthcheck": { "Test": ["CMD-SHELL", "curl -f http://localhost/ || exit 1"] } },
          "HostConfig": { "RestartPolicy": { "Name": "unless-stopped", "MaximumRetryCount": 0 }, "Memory": 536870912, "NanoCpus": 1500000000,
                          "Privileged": true, "CapAdd": ["NET_ADMIN"], "NetworkMode": "shop_default",
                          "LogConfig": { "Type": "json-file", "Config": { "max-size": "10m" } }, "Dns": ["1.1.1.1"], "ExtraHosts": ["db:10.0.0.5"] },
          "NetworkSettings": { "Networks": { "shop_default": { "IPAddress": "172.20.0.3", "Gateway": "172.20.0.1", "MacAddress": "02:42:ac:14:00:03",
                                                                 "GlobalIPv6Address": "", "Aliases": ["web", "shop-web-1"] } } }
        }]
        """;

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("manage-host");
            App.Hosts.Containers.AddRange(
            [
                Container("webid0000001", "running", "Up 3 hours (unhealthy)", "shop-web-1", "com.docker.compose.project=shop,com.docker.compose.service=web"),
                Container("oldid0000002", "exited", "Exited (0) 2 days ago", "leftover", string.Empty),
            ]);
            App.Hosts.Respond = command =>
                command.Contains(" inspect --type container ", StringComparison.Ordinal) ? InspectJson
                : command.Contains(" top ", StringComparison.Ordinal) ? "UID PID PPID C STIME TTY TIME CMD\nroot 1 0 0 08:30 ? 00:00:01 nginx: master process nginx -g daemon off;\n"
                : command.Contains(" container prune ", StringComparison.Ordinal) ? "Deleted Containers:\n" + new string('a', 64) + "\n\nTotal reclaimed space: 1.2MB\n"
                : null;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();

        private static string Container(string id, string state, string status, string name, string labels)
            => JsonSerializer.Serialize(new { ID = id, Names = name, Image = "nginx:1.27", State = state, Status = status, Labels = labels, Ports = string.Empty });
    }

    private readonly Site _site;

    public ContainerManageTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(UserRole role = UserRole.Admin)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        if (role == UserRole.Admin)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        }
        else
        {
            var name = "op" + Guid.NewGuid().ToString("N")[..8];
            SignIn.AddUser(_site.App, name, "UserPass123!", role);
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        }

        return browser;
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string handler, params (string Key, string Value)[] fields)
    {
        var html = await (await browser.GetAsync(page)).Content.ReadAsStringAsync();
        var data = fields.ToDictionary(f => f.Key, f => f.Value);
        data["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html);
        var separator = page.Contains('?') ? "&" : "?";
        return await browser.PostAsync($"{page}{separator}handler={handler}", new FormUrlEncodedContent(data));
    }

    // ---- reading docker inspect ----

    [Fact]
    public void Inspect_reads_state_health_command_limits_and_networks()
    {
        var i = ContainerInspect.Parse(InspectJson)!;

        Assert.Equal("shop-web-1", i.Name);
        Assert.Equal("nginx:1.27", i.Image);
        Assert.True(i.Running);
        Assert.Null(i.FinishedAt); // 0001-01-01 is "never"
        Assert.Equal(("unless-stopped", 3), (i.RestartPolicy, i.RestartCount));
        Assert.Equal(("unhealthy", 2), (i.HealthStatus, i.HealthFailingStreak));
        Assert.Equal("curl -f http://localhost/ || exit 1", i.HealthTest);
        Assert.Equal("curl: (7) Failed to connect", Assert.Single(i.HealthLog).Output);
        Assert.Equal("/docker-entrypoint.sh nginx -g daemon off;", string.Join(' ', i.Entrypoint.Concat(i.Cmd)));
        Assert.Equal((536870912L, 1.5), (i.MemoryBytes, i.Cpus));
        Assert.True(i.Privileged);
        Assert.Equal(new[] { "NET_ADMIN" }, i.CapAdd);
        Assert.Equal(("shop", "web"), (i.Project, i.Service));
        var net = Assert.Single(i.Networks);
        Assert.Equal(("shop_default", "172.20.0.3"), (net.Name, net.IpAddress));
        Assert.Equal(new[] { "web", "shop-web-1" }, net.Aliases);
    }

    [Fact]
    public void Secret_looking_variables_are_masked_in_the_list_and_in_the_raw_answer()
    {
        var i = ContainerInspect.Parse(InspectJson)!;

        Assert.Equal("Europe/Berlin", i.Env.Single(e => e.Name == "TZ").Value);
        Assert.All(i.Env.Where(e => e.Name is "DB_PASSWORD" or "API_KEY"), e => Assert.Equal((ContainerInspect.Mask, true), (e.Value, e.Masked)));
        Assert.False(i.Env.Single(e => e.Name == "EMPTY_SECRET").Masked); // nothing to hide

        Assert.DoesNotContain("hunter2", i.RawJson);
        Assert.DoesNotContain("abc123", i.RawJson);
        Assert.Contains("\"DB_PASSWORD=" + ContainerInspect.Mask + "\"", i.RawJson);
        Assert.Contains("TZ=Europe/Berlin", i.RawJson);
    }

    [Fact]
    public void Top_keeps_the_spaces_of_the_command()
    {
        var top = ContainerInspect.ParseTop("UID PID PPID C STIME TTY TIME CMD\nroot 1 0 0 08:30 ? 00:00:01 nginx: master process nginx -g daemon off;\n");

        Assert.Equal(8, top.Columns.Count);
        Assert.Equal("nginx: master process nginx -g daemon off;", Assert.Single(top.Rows)[7]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Name\":\"x\"}")]
    public void Something_that_is_no_container_is_null(string json) => Assert.Null(ContainerInspect.Parse(json));

    // ---- the commands ----

    [Fact]
    public void The_commands_quote_the_id_and_check_the_new_name()
    {
        Assert.EndsWith("docker rm -f -v 'web1' 2>&1", ContainerCommands.Remove("docker", "web1", force: true, volumes: true));
        Assert.EndsWith("docker rm 'web1' 2>&1", ContainerCommands.Remove("docker", "web1", force: false, volumes: false));
        Assert.EndsWith("docker rename 'web1' 'web2' 2>&1", ContainerCommands.Rename("docker", "web1", "web2"));
        Assert.EndsWith("docker container prune -f 2>&1", ContainerCommands.PruneStopped("docker"));
        Assert.Throws<ArgumentException>(() => ContainerCommands.Rename("docker", "web1", "x'; rm -rf /"));
        Assert.Throws<ArgumentException>(() => ContainerCommands.Remove("docker", "a b", true, true));
    }

    // ---- the pages ----

    [Fact]
    public async Task The_detail_page_shows_the_tabs_and_never_a_secret()
    {
        var browser = await SignInAsync();

        var html = await (await browser.GetAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=webid0000001")).Content.ReadAsStringAsync();

        foreach (var tab in new[] { "tab-overview", "tab-env", "tab-net", "tab-mounts", "tab-health", "tab-top", "tab-inspect" })
        {
            Assert.Contains($"id=\"{tab}\"", html);
        }

        Assert.Contains("unless-stopped", html);
        Assert.Contains("172.20.0.3", html);
        Assert.Contains("Failed to connect", html);
        Assert.Contains("nginx: master process", html);
        Assert.Contains("Europe/Berlin", html);
        Assert.DoesNotContain("hunter2", html);
        Assert.DoesNotContain("abc123", html);
        Assert.Contains("handler=Remove", html);
        Assert.Contains("name=\"newName\"", html);
    }

    [Fact]
    public async Task Remove_from_the_detail_page_forces_and_takes_the_anonymous_volumes()
    {
        var browser = await SignInAsync();
        _site.App.Hosts.Commands.Clear();

        var post = await PostAsync(browser, $"/Containers/ContainerDetail?envId={_site.EnvId}&id=webid0000001", "Remove",
            ("envId", _site.EnvId.ToString()), ("id", "webid0000001"), ("name", "shop-web-1"));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/Containers", SignIn.Path(post));
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" rm -f -v 'webid0000001'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_runs_docker_rename_and_a_bad_name_never_reaches_docker()
    {
        var browser = await SignInAsync();
        _site.App.Hosts.Commands.Clear();
        var page = $"/Containers/ContainerDetail?envId={_site.EnvId}&id=webid0000001";

        await PostAsync(browser, page, "Rename", ("envId", _site.EnvId.ToString()), ("id", "webid0000001"), ("newName", "shop-web-new"));
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" rename 'webid0000001' 'shop-web-new'", StringComparison.Ordinal));

        _site.App.Hosts.Commands.Clear();
        await PostAsync(browser, page, "Rename", ("envId", _site.EnvId.ToString()), ("id", "webid0000001"), ("newName", "evil'; touch /tmp/x"));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" rename ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_list_removes_a_stopped_container_and_prunes_all_stopped_ones()
    {
        var browser = await SignInAsync();
        var html = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"Remove leftover\"", html);       // the stopped one has a remove button
        Assert.DoesNotContain("aria-label=\"Remove shop-web-1\"", html); // the running one has not
        Assert.Contains("Remove stopped (1)", html);

        _site.App.Hosts.Commands.Clear();
        await PostAsync(browser, "/Containers", "Remove", ("envId", _site.EnvId.ToString()), ("id", "oldid0000002"), ("name", "leftover"));
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" rm -v 'oldid0000002'", StringComparison.Ordinal));

        var prune = await PostAsync(browser, "/Containers", "Prune");
        Assert.Equal(HttpStatusCode.Redirect, prune.StatusCode);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" container prune -f", StringComparison.Ordinal));
        var after = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();
        Assert.Contains("Removed 1 stopped container(s). Total reclaimed space: 1.2MB.", System.Net.WebUtility.HtmlDecode(after));
    }

    [Theory]
    [InlineData("/Containers", "Remove")]
    [InlineData("/Containers", "Prune")]
    public async Task A_user_may_start_and_stop_but_not_remove_or_prune(string page, string handler)
    {
        var browser = await SignInAsync(UserRole.User);
        _site.App.Hosts.Commands.Clear();

        var post = await PostAsync(browser, page, handler, ("envId", _site.EnvId.ToString()), ("id", "oldid0000002"));

        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" rm ", StringComparison.Ordinal) || c.Contains(" prune", StringComparison.Ordinal));
    }
}
