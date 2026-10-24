using System.Net;
using System.Text.Json;
using MatDock.Core.Containers;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The full container form, <c>docker update</c>, and a container read back into its settings: duplicated (secrets stay on the server) and recreated (with a way back).</summary>
public sealed class ContainerDuplicateTests : IClassFixture<ContainerDuplicateTests.Site>
{
    private const string Inspect = """
        [{
          "Id": "appid0000000000000000000000000000000000000000000000000000000000001",
          "Name": "/app",
          "Image": "sha256:img",
          "RestartCount": 0,
          "State": { "Status": "running", "Running": true },
          "Config": { "Hostname": "appid0000000", "Image": "ghcr.io/acme/app:1.2", "User": "", "WorkingDir": "/srv",
                      "Env": ["PATH=/usr/local/bin:/usr/bin", "DB_PASSWORD=hunter2", "MODE=prod"],
                      "Cmd": ["serve", "--port", "8080"], "Entrypoint": ["/entry.sh"],
                      "Labels": { "org.opencontainers.image.title": "app", "team": "core", "com.docker.compose.version": "2.29.7" },
                      "Healthcheck": { "Test": ["CMD-SHELL", "curl -f http://localhost:8080/ || exit 1"], "Interval": 30000000000, "Retries": 3 } },
          "HostConfig": { "RestartPolicy": { "Name": "unless-stopped" }, "Memory": 1073741824, "NanoCpus": 500000000, "Privileged": false,
                          "CapAdd": ["NET_ADMIN"], "CapDrop": ["MKNOD"], "NetworkMode": "backend",
                          "PortBindings": { "8080/tcp": [ { "HostIp": "", "HostPort": "18080" }, { "HostIp": "::", "HostPort": "18080" } ],
                                            "53/udp": [ { "HostIp": "127.0.0.1", "HostPort": "5353" } ] },
                          "LogConfig": { "Type": "json-file", "Config": { "max-size": "10m", "max-file": "3" } },
                          "Dns": ["1.1.1.1"], "ExtraHosts": ["db:10.0.0.5"],
                          "Devices": [ { "PathOnHost": "/dev/ttyUSB0", "PathInContainer": "/dev/ttyUSB0", "CgroupPermissions": "rwm" } ],
                          "DeviceRequests": [ { "Driver": "", "Count": -1, "DeviceIDs": null, "Capabilities": [["gpu"]] } ] },
          "Mounts": [ { "Type": "volume", "Name": "app-data", "Source": "/var/lib/docker/volumes/app-data/_data", "Destination": "/data", "RW": true },
                      { "Type": "volume", "Name": "3f9a8b7c6d", "Source": "/var/lib/docker/volumes/3f9a/_data", "Destination": "/cache", "RW": true },
                      { "Type": "bind", "Source": "/etc/app", "Destination": "/config", "RW": false } ],
          "NetworkSettings": { "Networks": { "backend": { "IPAddress": "172.30.0.9", "IPAMConfig": { "IPv4Address": "172.30.0.9" },
                                                          "Aliases": ["app", "appid0000000", "api"] } } }
        }]
        """;

    private const string ImageConfigJson = """
        {"Env":["PATH=/usr/local/bin:/usr/bin"],"Labels":{"org.opencontainers.image.title":"app"},"Cmd":["serve"],"Entrypoint":["/entry.sh"],"WorkingDir":"/srv","User":""}
        """;

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("dup-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new
            {
                ID = "appid0000001", Names = "app", Image = "ghcr.io/acme/app:1.2", State = "running", Status = "Up 2 hours", Labels = string.Empty, Ports = string.Empty,
            }));
            App.Hosts.Respond = command =>
                command.Contains(" inspect --type container ", StringComparison.Ordinal) ? Inspect
                : command.Contains(" image inspect --format '{{json .Config}}'", StringComparison.Ordinal) ? ImageConfigJson
                : null;
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public ContainerDuplicateTests(Site site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    // ---- the command ----

    [Fact]
    public void Every_new_setting_reaches_docker_run_quoted()
    {
        var spec = new ContainerRunSpec
        {
            Image = "nginx:1.27", Name = "web", Network = "backend", Ip = "172.30.0.10", NetworkAliases = ["api"],
            Command = ["--conf", "/etc/x y.conf"], Entrypoint = "/entry.sh", Labels = ["team=core"], Memory = "512M", Cpus = "1.5",
            Hostname = "web1", User = "1000:1000", WorkDir = "/srv", Privileged = true, CapAdd = ["NET_ADMIN"], CapDrop = ["MKNOD"],
            Devices = ["/dev/ttyUSB0"], Gpus = "all", LogDriver = "json-file", LogMaxSize = "10m", LogMaxFile = 3, Dns = ["1.1.1.1"],
            ExtraHosts = ["db:10.0.0.5", "host:host-gateway"], HealthCmd = "curl -f http://localhost/ || exit 1", HealthInterval = "30s", HealthRetries = 3,
        };

        var cmd = ContainerRunCommands.Run(spec);

        foreach (var part in new[]
        {
            "--network 'backend'", "--ip '172.30.0.10'", "--network-alias 'api'", "--label 'team=core'", "--entrypoint '/entry.sh'",
            "--memory '512m'", "--cpus '1.5'", "--hostname 'web1'", "--user '1000:1000'", "--workdir '/srv'", "--privileged",
            "--cap-add 'NET_ADMIN'", "--cap-drop 'MKNOD'", "--device '/dev/ttyUSB0'", "--gpus 'all'", "--log-driver 'json-file'",
            "--log-opt 'max-size=10m'", "--log-opt 'max-file=3'", "--dns '1.1.1.1'", "--add-host 'db:10.0.0.5'", "--add-host 'host:host-gateway'",
            "--health-cmd 'curl -f http://localhost/ || exit 1'", "--health-interval '30s'", "--health-retries 3",
            "'nginx:1.27' '--conf' '/etc/x y.conf'",
        })
        {
            Assert.Contains(part, cmd);
        }
    }

    [Theory]
    [InlineData("Memory", "lots")]
    [InlineData("Cpus", "0")]
    [InlineData("Hostname", "-bad")]
    [InlineData("User", "root; id")]
    [InlineData("WorkDir", "relative")]
    [InlineData("Gpus", "every")]
    [InlineData("LogDriver", "fluentd")]
    [InlineData("HealthInterval", "30")]
    [InlineData("Ip", "172.30.0.10")]          // without a network
    [InlineData("Entrypoint", "a\nb")]
    public void A_bad_value_is_refused(string field, string value)
    {
        var spec = new ContainerRunSpec { Image = "nginx" };
        typeof(ContainerRunSpec).GetProperty(field)!.SetValue(spec, value);

        Assert.NotNull(ContainerRunCommands.Validate(spec));
    }

    [Fact]
    public void Bad_list_entries_are_refused()
    {
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", CapAdd = ["net admin"] }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", Devices = ["/etc/passwd"] }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", Dns = ["dns.google"] }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", ExtraHosts = ["db"] }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", Labels = ["=x"] }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", Command = ["a\nb"] }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", LogDriver = "syslog", LogMaxSize = "10m" }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "nginx", NetworkAliases = ["api"] }));
    }

    [Fact]
    public void Docker_update_sets_limits_and_restart_and_unlimited_swap_with_memory()
    {
        Assert.EndsWith("docker update --memory '1g' --memory-swap -1 --cpus '2' --restart 'always' 'web1' 2>&1",
            ContainerRunCommands.Update("docker", "web1", new ContainerUpdateSpec("1G", "2", "always")));
        Assert.EndsWith("docker update --cpus '0.5' 'web1' 2>&1", ContainerRunCommands.Update("docker", "web1", new ContainerUpdateSpec(null, "0.5", null)));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerUpdateSpec(null, null, null)));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerUpdateSpec("x", null, null)));
        Assert.Null(ContainerRunCommands.Validate(new ContainerUpdateSpec("0", "0", null))); // 0 removes a limit
    }

    // ---- a container read back ----

    [Fact]
    public void A_container_is_read_back_into_the_settings_that_differ_from_its_image()
    {
        var spec = ContainerRunSpecs.FromInspect(ContainerInspect.Parse(Inspect, maskSecrets: false)!, ImageConfig.Parse(ImageConfigJson));

        Assert.Equal(("ghcr.io/acme/app:1.2", "app"), (spec.Image, spec.Name));
        Assert.Equal(new[] { "DB_PASSWORD=hunter2", "MODE=prod" }, spec.Env);             // PATH is the image's
        Assert.Equal(new[] { "team=core" }, spec.Labels);                                  // not the image's, not compose's
        Assert.Equal(new[] { "18080:8080", "127.0.0.1:5353:53/udp" }, spec.Ports);         // the IPv6 twin is one binding
        Assert.Equal(new[] { "app-data:/data", "3f9a8b7c6d:/cache", "/etc/app:/config:ro" }, spec.Volumes); // the anonymous one by name: its data stays
        Assert.Equal(("unless-stopped", "backend", "172.30.0.9"), (spec.Restart, spec.Network, spec.Ip));
        Assert.Equal(new[] { "api" }, spec.NetworkAliases);                                // not its name or id
        Assert.Null(spec.Entrypoint);                                                      // the image's
        Assert.Equal(new[] { "serve", "--port", "8080" }, spec.Command);                   // differs from the image's "serve"
        Assert.Equal(("1024m", "0.5"), (spec.Memory, spec.Cpus));
        Assert.Null(spec.Hostname);                                                        // the default (the short id)
        Assert.Null(spec.WorkDir);
        Assert.Equal(new[] { "NET_ADMIN" }, spec.CapAdd);
        Assert.Equal(new[] { "MKNOD" }, spec.CapDrop);
        Assert.Equal(new[] { "/dev/ttyUSB0:/dev/ttyUSB0" }, spec.Devices);
        Assert.Equal("all", spec.Gpus);
        Assert.Equal(("json-file", "10m", 3), (spec.LogDriver, spec.LogMaxSize, spec.LogMaxFile));
        Assert.Equal(("curl -f http://localhost:8080/ || exit 1", "30s", 3), (spec.HealthCmd, spec.HealthInterval, spec.HealthRetries));
        Assert.Null(ContainerRunCommands.Validate(spec)); // it can be run again as it is
    }

    // ---- the pages ----

    [Fact]
    public async Task A_duplicate_shows_the_settings_with_the_secret_masked_and_runs_with_the_real_value()
    {
        var browser = await AdminAsync();

        var form = await (await browser.GetAsync($"/Containers/Create?from=appid0000001&envId={_site.EnvId}")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"app-copy\"", form);
        Assert.Contains("DB_PASSWORD=" + ContainerInspect.Mask, System.Net.WebUtility.HtmlDecode(form));
        Assert.DoesNotContain("hunter2", form);

        _site.App.Hosts.Commands.Clear();
        var post = await browser.PostAsync("/Containers/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(form),
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.FromId"] = "appid0000001",
            ["Input.Image"] = "ghcr.io/acme/app:1.2",
            ["Input.Name"] = "app-copy",
            ["Input.Env"] = "DB_PASSWORD=" + ContainerInspect.Mask + "\nMODE=prod",
            ["Input.Restart"] = "no",
        }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var run = Assert.Single(_site.App.Hosts.Commands, c => c.Contains(" run -d ", StringComparison.Ordinal));
        Assert.Contains("-e 'DB_PASSWORD=hunter2'", run);
        Assert.DoesNotContain(ContainerInspect.Mask, run);
    }

    [Fact]
    public async Task A_masked_value_without_its_source_is_refused()
    {
        var browser = await AdminAsync();
        var form = await (await browser.GetAsync("/Containers/Create")).Content.ReadAsStringAsync();
        _site.App.Hosts.Commands.Clear();

        var post = await browser.PostAsync("/Containers/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(form),
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Image"] = "nginx",
            ["Input.Env"] = "DB_PASSWORD=" + ContainerInspect.Mask,
            ["Input.Restart"] = "no",
        }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("still has the masked value", await post.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" run -d ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recreate_parks_the_old_one_runs_the_new_one_and_removes_the_old_one()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=appid0000001")).Content.ReadAsStringAsync();
        Assert.Contains("handler=Recreate", page);
        _site.App.Hosts.Commands.Clear();

        var post = await browser.PostAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=appid0000001&handler=Recreate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["envId"] = _site.EnvId.ToString(),
            ["id"] = "appid0000001",
            ["pull"] = "true",
        }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var steps = _site.App.Hosts.Commands.Where(c => c.Contains(" pull ") || c.Contains(" rename ") || c.Contains(" stop ") || c.Contains(" run -d ") || c.Contains(" rm ")).ToList();
        Assert.Contains(" pull 'ghcr.io/acme/app:1.2'", steps[0]);
        Assert.Matches(" rename '[a-z0-9]+' 'app-old-\\d{14}'", steps[1]);
        Assert.Contains(" stop ", steps[2]);
        Assert.Contains("--name 'app'", steps[3]);
        Assert.Contains("-v '3f9a8b7c6d:/cache'", steps[3]); // the anonymous volume goes along
        Assert.Contains("-e 'DB_PASSWORD=hunter2'", steps[3]);
        Assert.Matches(" rm -f '[a-z0-9]+'", steps[4]);         // without -v: the volumes are the new one's
        Assert.DoesNotContain(" rm -f -v ", steps[4]);
    }

    [Fact]
    public async Task A_new_container_that_does_not_start_brings_the_old_one_back()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=appid0000001")).Content.ReadAsStringAsync();
        _site.App.Hosts.Commands.Clear();
        _site.App.Hosts.Fails = c => c.Contains(" run -d ", StringComparison.Ordinal);
        try
        {
            await browser.PostAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=appid0000001&handler=Recreate", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
                ["envId"] = _site.EnvId.ToString(),
                ["id"] = "appid0000001",
                ["pull"] = "false",
            }));
        }
        finally
        {
            _site.App.Hosts.Fails = null;
        }

        var after = _site.App.Hosts.Commands.SkipWhile(c => !c.Contains(" run -d ", StringComparison.Ordinal)).ToList();
        Assert.Contains(after, c => c.Contains(" rm -f 'app'", StringComparison.Ordinal));        // the half-made new one
        Assert.Contains(after, c => System.Text.RegularExpressions.Regex.IsMatch(c, " rename '[a-z0-9]+' 'app'"));   // the old one gets its name back
        Assert.Contains(after, c => c.Contains(" start ", StringComparison.Ordinal));               // and runs again
    }

    [Fact]
    public async Task The_limits_form_runs_docker_update()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=appid0000001")).Content.ReadAsStringAsync();
        _site.App.Hosts.Commands.Clear();

        await browser.PostAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=appid0000001&handler=Limits", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["envId"] = _site.EnvId.ToString(),
            ["id"] = "appid0000001",
            ["memory"] = "2g",
            ["cpus"] = "1,5",
            ["restart"] = "",
        }));

        Assert.Contains(_site.App.Hosts.Commands, c => c.Contains(" update --memory '2g' --memory-swap -1 --cpus '1.5' 'appid0000001'", StringComparison.Ordinal));
    }
}
