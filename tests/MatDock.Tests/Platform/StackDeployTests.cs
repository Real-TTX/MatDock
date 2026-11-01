using System.Net;
using System.Text.Json;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// Deploys of a managed stack: the options become the compose steps, every deploy lands in the history with its trigger and who started it, the
/// editor and the list deploy in the background and follow it in a console, and the Deploys tab shows the history.
/// </summary>
public sealed class StackDeployTests : IClassFixture<StackDeployTests.Site>
{
    private const string Compose = "services:\n  web:\n    image: nginx:alpine\n";

    private const string ComposeOutput =
        " Network shop_default  Creating\n Network shop_default  Created\n"
        + " Container shop-db-1  Running\n"
        + " Container shop-web-1  Recreate\n Container shop-web-1  Recreated\n Container shop-web-1  Starting\n Container shop-web-1  Started\n"
        + " Container shop-cache-1  Creating\n Container shop-cache-1  Created\n Container shop-cache-1  Starting\n Container shop-cache-1  Started\n";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("deploy-host");
            App.Hosts.Respond = c => c.Contains(" up -d", StringComparison.Ordinal) ? ComposeOutput : null;
            App.Hosts.Fails = c => c.Contains("broken-stack", StringComparison.Ordinal) && c.Contains(" up -d", StringComparison.Ordinal);
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public StackDeployTests(Site site) => _site = site;

    private long CreateStack(string name, DeployOptions? options = null)
    {
        using var scope = _site.App.Services.CreateScope();
        var stacks = scope.ServiceProvider.GetRequiredService<StackService>();
        var (ok, message, id) = stacks.CreateAsync(new StackInput { Name = name, EnvironmentId = _site.EnvId, ComposeYaml = Compose }).GetAwaiter().GetResult();
        Assert.True(ok, message);
        if (options is not null)
        {
            stacks.SetDeployOptionsAsync(id, options).GetAwaiter().GetResult();
        }

        return id;
    }

    private async Task<(bool Ok, string Output)> DeployAsync(long id, DeployRequest request)
    {
        using var scope = _site.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StackService>().DeployAsync(id, request);
    }

    private async Task<(HttpClient Browser, string Html)> AdminAsync(string page)
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        return (browser, await (await browser.GetAsync(page)).Content.ReadAsStringAsync());
    }

    private async Task WaitAsync(string location)
    {
        var op = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + location).Query)["op"];
        Assert.False(string.IsNullOrEmpty(op), location);
        await _site.App.Services.GetRequiredService<OperationRegistry>().WaitAsync(op!);
    }

    // ---- the compose steps ----

    [Fact]
    public void The_options_become_the_steps_of_the_deploy()
    {
        var all = new DeployOptions { Pull = true, Build = true, PullBase = true, NoCache = true, ForceRecreate = true, RemoveOrphans = true };
        var inline = StackCommands.Deploy("docker", "shop", all);
        Assert.Contains("docker compose -p \"$name\" $cf pull; ", inline);
        Assert.Contains("docker compose -p \"$name\" $cf build --pull --no-cache; ", inline);
        Assert.EndsWith("docker compose -p \"$name\" $cf up -d --force-recreate --remove-orphans' 2>&1", inline);
        Assert.True(inline.IndexOf(" pull; ", StringComparison.Ordinal) < inline.IndexOf(" build ", StringComparison.Ordinal));

        var git = StackCommands.GitSync("docker", "shop", "deploy/compose.yml", new DeployOptions { Build = true, ForceRecreate = true });
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" $ov build; ", git);
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" $ov up -d --force-recreate", git);
        Assert.DoesNotContain(" pull", git);
    }

    [Fact]
    public void Without_options_the_deploy_and_the_update_stay_as_they_were()
    {
        Assert.Equal(StackCommands.Deploy("docker", "shop", DeployOptions.None), StackCommands.Deploy("docker", "shop"));
        Assert.EndsWith("docker compose -p \"$name\" $cf up -d' 2>&1", StackCommands.Deploy("docker", "shop"));
        Assert.DoesNotContain(" build", StackCommands.Deploy("docker", "shop"));
        Assert.Contains(" pull; ", StackCommands.Update("docker", "shop"));
        Assert.Equal(StackCommands.GitSync("docker", "shop", "c.yml", pull: true), StackCommands.GitSync("docker", "shop", "c.yml", new DeployOptions { Pull = true }));
    }

    [Fact]
    public void The_options_are_kept_as_json_and_read_back()
    {
        Assert.Null(DeployOptions.None.ToJson());
        Assert.Equal(DeployOptions.None, DeployOptions.FromJson(null));
        Assert.Equal(DeployOptions.None, DeployOptions.FromJson("{not json"));

        var options = new DeployOptions { Build = true, NoCache = true, RemoveOrphans = true };
        Assert.Equal(options, DeployOptions.FromJson(options.ToJson()));
        Assert.Equal("build (no cache), remove orphans", options.Describe());
        Assert.True(options.Builds);
        Assert.True(new DeployOptions { PullBase = true }.Builds);
        Assert.Equal(string.Empty, DeployOptions.None.Describe());
    }

    [Fact]
    public void The_summary_says_what_compose_did_to_the_containers()
    {
        Assert.Equal("1 recreated, 1 created, 1 unchanged.", DeploymentHistory.Summarize(true, ComposeOutput));
        Assert.Equal("2 pulled, 1 started.", DeploymentHistory.Summarize(true, " web Pulled\n db Pulled\n Container a-web-1  Started\n"));
        Assert.Equal("Deployed.", DeploymentHistory.Summarize(true, "OK."));
        Assert.Equal("1 built, 1 created.", DeploymentHistory.Summarize(true, " Image md-buildtest:local Built\n Container md-buildtest-app-1  Created\n Container md-buildtest-app-1  Started\n"));
        Assert.Equal("Error response from daemon: port is already allocated",
            DeploymentHistory.Summarize(false, " Container a-web-1  Starting\nError response from daemon: port is already allocated\n"));
    }

    // ---- the history ----

    [Fact]
    public async Task A_deploy_is_recorded_with_its_trigger_actor_options_and_outcome()
    {
        var id = CreateStack("hist-ok", new DeployOptions { ForceRecreate = true });

        var (ok, _) = await DeployAsync(id, new DeployRequest(Trigger: DeployTrigger.Api, Actor: "API key ci"));
        Assert.True(ok);

        var row = _site.App.Db(db => db.StackDeployments.Single(d => d.StackId == id));
        Assert.Equal(DeployTrigger.Api, row.Trigger);
        Assert.Equal("API key ci", row.Actor);
        Assert.Equal("force recreate", row.Options);
        Assert.True(row.Ok);
        Assert.NotNull(row.FinishedAt);
        Assert.Equal("1 recreated, 1 created, 1 unchanged.", row.Summary);
        Assert.Contains("shop-web-1", row.Log);

        // The stack's own option reached compose.
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains("hist-ok", StringComparison.Ordinal) && c.Contains("up -d --force-recreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_deploy_is_recorded_as_failed()
    {
        var id = CreateStack("broken-stack");
        var (ok, _) = await DeployAsync(id, new DeployRequest(Trigger: DeployTrigger.Webhook));
        Assert.False(ok);

        var row = _site.App.Db(db => db.StackDeployments.Single(d => d.StackId == id));
        Assert.False(row.Ok);
        Assert.Equal(DeployTrigger.Webhook, row.Trigger);
    }

    [Fact]
    public async Task An_update_pulls_and_ignores_the_stacks_build_option()
    {
        var id = CreateStack("hist-update", new DeployOptions { Build = true });
        using (var scope = _site.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<StackService>().UpdateAsync(id, new DeployRequest(Trigger: DeployTrigger.Update));
        }

        var row = _site.App.Db(db => db.StackDeployments.Single(d => d.StackId == id));
        Assert.Equal("pull", row.Options);
        Assert.Equal(DeployTrigger.Update, row.Trigger);
    }

    [Fact]
    public async Task Only_the_newest_deploys_of_a_stack_are_kept()
    {
        var id = CreateStack("hist-trim");
        for (var i = 0; i < DeploymentHistory.Keep + 3; i++)
        {
            await DeployAsync(id, DeployRequest.Default);
        }

        Assert.Equal(DeploymentHistory.Keep, _site.App.Db(db => db.StackDeployments.Count(d => d.StackId == id)));
    }

    // ---- the pages ----

    [Fact]
    public async Task Save_and_deploy_runs_in_the_background_with_the_options_and_shows_the_console()
    {
        var (browser, html) = await AdminAsync("/Stacks/Edit");
        var response = await browser.PostAsync("/Stacks/Edit?handler=SaveDeploy", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.Name"] = "page-deploy",
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Source"] = "inline",
            ["Input.ComposeYaml"] = Compose,
            ["Input.OptPull"] = "true",
            ["Input.OptRemoveOrphans"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = SignIn.Path(response);
        Assert.Contains("op=", location);
        await WaitAsync(location);

        var stack = _site.App.Db(db => db.Stacks.Single(s => s.Name == "page-deploy"));
        Assert.Equal(new DeployOptions { Pull = true, RemoveOrphans = true }, DeployOptions.FromJson(stack.DeployOptionsJson));
        var row = _site.App.Db(db => db.StackDeployments.Single(d => d.StackId == stack.Id));
        Assert.Equal(DeployTrigger.Manual, row.Trigger);
        Assert.Equal(MatDockFactory.Admin, row.Actor);
        Assert.Equal("pull, remove orphans", row.Options);

        var page = await (await browser.GetAsync(location)).Content.ReadAsStringAsync();
        Assert.Contains("data-console=\"/ui/operations/", page);
        Assert.Contains("js/console.js", page);
        Assert.Contains("checked=\"checked\"", page);

        var op = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + location).Query)["op"];
        var json = JsonDocument.Parse(await browser.GetStringAsync($"/ui/operations/{op}?offset=0")).RootElement;
        Assert.True(json.GetProperty("finished").GetBoolean());
        Assert.True(json.GetProperty("ok").GetBoolean());

        var deploys = await (await browser.GetAsync($"/Stacks/Deploys?id={stack.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Manual", deploys);
        Assert.Contains("pull, remove orphans", deploys);
        Assert.Contains(MatDockFactory.Admin, deploys);
    }

    [Fact]
    public async Task The_list_deploys_in_the_background_with_the_stacks_options()
    {
        var id = CreateStack("list-deploy", new DeployOptions { Build = true });
        var (browser, html) = await AdminAsync("/Stacks");
        var response = await browser.PostAsync($"/Stacks?handler=Deploy&id={id}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await WaitAsync(SignIn.Path(response));

        var row = _site.App.Db(db => db.StackDeployments.Single(d => d.StackId == id));
        Assert.Equal("build", row.Options);
        Assert.True(row.Ok);
        Assert.Contains("data-console=", await (await browser.GetAsync(SignIn.Path(response))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_console_endpoint_needs_a_sign_in_and_knows_only_running_operations()
    {
        var anonymous = await _site.App.Browser().GetAsync("/ui/operations/abc");
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);

        var (browser, _) = await AdminAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/ui/operations/" + Guid.NewGuid().ToString("N"))).StatusCode);
    }

    [Fact]
    public async Task The_deploys_tab_lists_the_history_and_shows_one_log()
    {
        var id = CreateStack("tab-history");
        await DeployAsync(id, new DeployRequest(Trigger: DeployTrigger.Schedule, Actor: "Nightly"));
        var rowId = _site.App.Db(db => db.StackDeployments.Single(d => d.StackId == id).Id);

        var (browser, _) = await AdminAsync("/");
        var page = await (await browser.GetAsync($"/Stacks/Deploys?id={id}&show={rowId}")).Content.ReadAsStringAsync();
        Assert.Contains("Schedule", page);
        Assert.Contains("Nightly", page);
        Assert.Contains("1 recreated, 1 created, 1 unchanged.", page);
        Assert.Contains("shop-cache-1", page); // the log
        Assert.Contains("href=\"/Stacks/Update?id=" + id, page); // the tabs
    }
}
