using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>One service of a stack restarted, recreated or scaled; the redeploy webhook a CI calls; a stack copied into another environment.</summary>
public sealed class StackServiceActionTests : IClassFixture<StackServiceActionTests.Site>
{
    private const string Compose = "services:\n  web:\n    image: nginx:1.27\n  worker:\n    image: busybox\n";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("stack-host");
            OtherEnvId = App.AddLocalEnvironment("other-host");
            App.Api(MatDockFactory.EngineKey).PutAsJsonAsync("/api/v1/stacks/shop", new { composeYaml = Compose, envContent = "DB_PASSWORD=hunter2" }).GetAwaiter().GetResult().EnsureSuccessStatusCode();
            StackId = App.Db(db => db.Stacks.Single(s => s.Name == "shop").Id);
            App.Hosts.Containers.AddRange(
            [
                Container("web000000001", "web"),
                Container("wrk000000001", "worker"),
            ]);
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }
        public long OtherEnvId { get; }
        public long StackId { get; }

        public void Dispose() => App.Dispose();

        private static string Container(string id, string service)
            => JsonSerializer.Serialize(new { ID = id, Names = $"shop-{service}-1", Image = "nginx:1.27", State = "running", Status = "Up 1 hour",
                Labels = $"com.docker.compose.project=shop,com.docker.compose.service={service}", Ports = string.Empty });
    }

    private readonly Site _site;

    public StackServiceActionTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(UserRole role = UserRole.Admin)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        if (role == UserRole.Admin)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
            return browser;
        }

        var name = "svc" + Guid.NewGuid().ToString("N")[..8];
        SignIn.AddUser(_site.App, name, "UserPass123!", role);
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        return browser;
    }

    // ---- the commands ----

    [Fact]
    public void A_service_command_passes_service_and_count_as_arguments()
    {
        var restart = StackCommands.Service("docker", "shop", "docker-compose.yml", "web", StackCommands.ServiceAction.Restart);
        Assert.Contains("compose -p \"$1\" -f \"$2\" $ov $ef restart \"$3\"", restart);
        Assert.Contains("else docker compose -p \"$1\" restart \"$3\"; fi", restart); // without the files: by the project label

        var scale = StackCommands.Service("docker", "shop", "docker-compose.yml", "web", StackCommands.ServiceAction.Scale, 3);
        Assert.Contains("up -d --no-recreate --scale \"$3=$4\" \"$3\"", scale);
        Assert.EndsWith("'shop' 'docker-compose.yml' 'web' 3 - 2>&1", scale);
        Assert.Contains("deploy it from MatDock once", scale);           // scale needs the files

        var recreate = StackCommands.Service("docker", "shop", "docker-compose.yml", "web", StackCommands.ServiceAction.Recreate);
        Assert.Contains("up -d --force-recreate --no-deps \"$3\"", recreate);

        Assert.Throws<ArgumentException>(() => StackCommands.Service("docker", "shop", "docker-compose.yml", "web; id", StackCommands.ServiceAction.Restart));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackCommands.Service("docker", "shop", "docker-compose.yml", "web", StackCommands.ServiceAction.Scale, 101));
    }

    [Fact]
    public void Scale_and_recreate_run_in_a_real_shell_with_the_override_file()
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("matdock-service").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "shop"));
            File.WriteAllText(Path.Combine(dir, "shop", "docker-compose.yml"), Compose);
            File.WriteAllText(Path.Combine(dir, "shop", "matdock-override.yml"), "services: {}\n");
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(StackCommands.Service("echo", "shop", "docker-compose.yml", "web", StackCommands.ServiceAction.Scale, 3));
            psi.Environment["MATDOCK_STACKS_DIR"] = dir;
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);

            Assert.Equal(0, p.ExitCode);
            Assert.Contains("compose -p shop -f docker-compose.yml -f matdock-override.yml up -d --no-recreate --scale web=3 web", output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the page ----

    private async Task<HttpResponseMessage> ServiceAsync(HttpClient browser, string service, string action, int replicas = 1)
    {
        var page = await (await browser.GetAsync($"/Containers/Stack?envId={_site.EnvId}&project=shop")).Content.ReadAsStringAsync();
        return await browser.PostAsync($"/Containers/Stack?envId={_site.EnvId}&project=shop&handler=Service", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["envId"] = _site.EnvId.ToString(),
            ["project"] = "shop",
            ["service"] = service,
            ["serviceAction"] = action,
            ["replicas"] = replicas.ToString(),
        }));
    }

    [Fact]
    public async Task The_stack_page_lists_the_services_and_scales_one()
    {
        var browser = await SignInAsync();
        var html = await (await browser.GetAsync($"/Containers/Stack?envId={_site.EnvId}&project=shop")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"Scale web\"", html);
        Assert.Contains("aria-label=\"Recreate worker\"", html);

        _site.App.Hosts.Commands.Clear();
        var post = await ServiceAsync(browser, "web", "Scale", 3);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains("--scale \"$3=$4\"", StringComparison.Ordinal) && c.EndsWith("'web' 3 x 2>&1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_cannot_touch_a_service()
    {
        var browser = await SignInAsync(UserRole.User);
        _site.App.Hosts.Commands.Clear();

        var post = await ServiceAsync(browser, "web", "Restart");

        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(post));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains("restart \"$3\"", StringComparison.Ordinal));
    }

    // ---- the webhook ----

    [Fact]
    public async Task The_webhook_redeploys_the_stack_and_only_with_its_secret()
    {
        using var scope = _site.App.Services.CreateScope();
        var stacks = scope.ServiceProvider.GetRequiredService<StackService>();
        var (_, token) = await stacks.SetWebhookAsync(_site.StackId, true);
        Assert.NotNull(token);
        Assert.True(token!.Length >= 30);

        var anonymous = _site.App.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/webhooks/stacks/" + new string('x', 32), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/webhooks/stacks/short", null)).StatusCode);

        _site.App.Hosts.Commands.Clear();
        var response = await anonymous.PostAsync("/webhooks/stacks/" + token, null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var opId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("operationId").GetString()!;
        await _site.App.Services.GetRequiredService<MatDock.Core.Operations.OperationRegistry>().WaitAsync(opId);

        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" pull;", StringComparison.Ordinal) || c.Contains(" pull $svcs", StringComparison.Ordinal));
        Assert.Contains(_site.App.Db(db => db.StackUpdateRuns.Where(r => r.StackId == _site.StackId).Select(r => r.Trigger).ToList()), t => t == "webhook");

        // Off: the old address is gone.
        await stacks.SetWebhookAsync(_site.StackId, false);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/webhooks/stacks/" + token, null)).StatusCode);
    }

    [Fact]
    public async Task The_update_page_switches_the_webhook_on_and_shows_its_address()
    {
        var browser = await SignInAsync();
        var page = await (await browser.GetAsync($"/Stacks/Update?id={_site.StackId}")).Content.ReadAsStringAsync();

        await browser.PostAsync($"/Stacks/Update?id={_site.StackId}&handler=Webhook", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["id"] = _site.StackId.ToString(),
            ["enabled"] = "true",
        }));

        var after = await (await browser.GetAsync($"/Stacks/Update?id={_site.StackId}")).Content.ReadAsStringAsync();
        var token = _site.App.Db(db => db.Stacks.Single(s => s.Id == _site.StackId).WebhookToken);
        Assert.Contains($"/webhooks/stacks/{token}", after);
    }

    // ---- copy ----

    [Fact]
    public async Task A_copy_takes_compose_env_overrides_and_pins_into_the_other_environment()
    {
        using var scope = _site.App.Services.CreateScope();
        var stacks = scope.ServiceProvider.GetRequiredService<StackService>();
        await stacks.SetImageOverrideAsync(_site.StackId, "web", "nginx:1.28");
        await stacks.SetPinnedAsync(_site.StackId, "worker", true);

        var (ok, message, copyId) = await stacks.CopyAsync(_site.StackId, _site.OtherEnvId, null, deploy: false);

        Assert.True(ok, message);
        var copy = _site.App.Db(db => db.Stacks.Single(s => s.Id == copyId));
        Assert.Equal(("shop", _site.OtherEnvId, Compose), (copy.Name, copy.EnvironmentId, copy.ComposeYaml));
        Assert.Equal("DB_PASSWORD=hunter2", (await stacks.GetAsync(copyId))!.EnvContent);
        Assert.Equal("nginx:1.28", ImageOverrides.Of(copy)["web"].Image);
        Assert.Equal("worker", copy.PinnedServices);
    }

    [Fact]
    public async Task A_copy_in_the_same_environment_needs_another_name()
    {
        using var scope = _site.App.Services.CreateScope();
        var stacks = scope.ServiceProvider.GetRequiredService<StackService>();

        Assert.False((await stacks.CopyAsync(_site.StackId, _site.EnvId, "shop", deploy: false)).Ok);
        var (ok, _, id) = await stacks.CopyAsync(_site.StackId, _site.EnvId, "shop-staging", deploy: false);
        Assert.True(ok);
        Assert.Equal("shop-staging", _site.App.Db(db => db.Stacks.Single(s => s.Id == id).Name));
    }

    [Fact]
    public async Task The_copy_page_copies_and_says_that_volumes_stay()
    {
        var browser = await SignInAsync();
        var page = await (await browser.GetAsync($"/Stacks/Copy?id={_site.StackId}")).Content.ReadAsStringAsync();
        Assert.Contains("The volumes are not", page);

        var post = await browser.PostAsync($"/Stacks/Copy?id={_site.StackId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["Id"] = _site.StackId.ToString(),
            ["TargetEnvironmentId"] = _site.EnvId.ToString(),
            ["Name"] = "shop-copy",
            ["Deploy"] = "false",
        }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.True(_site.App.Db(db => db.Stacks.Any(s => s.Name == "shop-copy" && s.EnvironmentId == _site.EnvId)));
    }

    [Fact]
    public async Task The_list_asks_the_host_again_right_after_a_stack_was_stopped()
    {
        var browser = await SignInAsync();
        var hosts = _site.App.Hosts;
        int Lists() { lock (hosts.Commands) { return hosts.Commands.Count(c => c.Contains(" ps -a --format", StringComparison.Ordinal)); } }

        var page = await (await browser.GetAsync("/Stacks")).Content.ReadAsStringAsync(); // the list is kept for 30 s from here
        var before = Lists();

        var stop = await browser.PostAsync("/Stacks?handler=Stop", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = _site.StackId.ToString(),
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
        }));
        Assert.Equal(HttpStatusCode.Redirect, stop.StatusCode);

        await browser.GetAsync("/Stacks");
        Assert.True(Lists() > before); // not the list from before the stop: the stack stood there as running for half a minute
    }
}
