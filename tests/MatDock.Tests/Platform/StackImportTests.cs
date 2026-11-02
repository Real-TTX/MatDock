using System.Net;
using MatDock.Core.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// Compose projects taken over from a folder of the host: their relative paths keep pointing to the original folder (else the data would be an empty
/// folder in MatDock's stack folder), their .env comes along, a folder is scanned for compose files and several are imported at once.
/// </summary>
public sealed class StackImportTests : IClassFixture<StackImportTests.Site>
{
    private const string Compose = "name: shop\nservices:\n  web:\n    build: .\n    env_file: app.env\n    volumes:\n      - ./data:/data\n      - cache:/cache\n      - /srv/abs:/abs\n";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("import-host");
            App.Hosts.Respond = c =>
            {
                if (c.Contains("find /scan", StringComparison.Ordinal))
                {
                    return "/scan/shop/compose.yml\n/scan/shop/compose.override.yml\n/scan/blog/docker-compose.yaml\n/scan/web/node_modules/x/compose.yml\n"
                           + "/scan/notes/config.yml\n/scan/running/compose.yml\n/scan/managed/compose.yml\n";
                }

                if (c.Contains("com.docker.compose.project", StringComparison.Ordinal) && c.Contains("ps -a --format", StringComparison.Ordinal))
                {
                    return "running\nrunning\nsomething-else\n";
                }

                if (c.Contains("cat \"/src/$2\"", StringComparison.Ordinal))
                {
                    if (c.Contains("'/opt/stacks/shop' 'compose.yml'", StringComparison.Ordinal)) { return Compose; }
                    if (c.Contains("'/opt/stacks/shop' '.env'", StringComparison.Ordinal)) { return "TZ=Europe/Berlin\n"; }
                    if (c.Contains("'/opt/stacks/blog' 'docker-compose.yaml'", StringComparison.Ordinal)) { return "services:\n  ghost:\n    image: ghost:5\n"; }
                }

                return null;
            };
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public StackImportTests(Site site) => _site = site;

    [Fact]
    public void Relative_paths_point_to_the_original_folder_and_the_rest_stays()
    {
        const string yaml = "# keep me\nservices:\n  web:\n    build:\n      context: ./app   # the app\n    env_file:\n      - .env.web\n      - /etc/abs.env\n"
                            + "    volumes:\n      - \"./data:/data:ro\"\n      - '../shared:/shared'\n      - named:/named\n      - /abs:/abs\n"
                            + "      - type: bind\n        source: ./conf\n        target: /conf\n      - type: volume\n        source: vol\n        target: /vol\n"
                            + "  db:\n    image: postgres:17\n    build: https://github.com/x/y.git\n"
                            + "configs:\n  site:\n    file: ./site.conf\n";
        var (result, changed) = ComposeRelocate.Absolutize(yaml, "/opt/stacks/shop");

        Assert.Equal(6, changed);
        Assert.Contains("# keep me", result);
        Assert.Contains("context: /opt/stacks/shop/app   # the app", result);
        Assert.Contains("- /opt/stacks/shop/.env.web", result);
        Assert.Contains("- /etc/abs.env", result);
        Assert.Contains("- \"/opt/stacks/shop/data:/data:ro\"", result);
        Assert.Contains("- '/opt/stacks/shared:/shared'", result);
        Assert.Contains("- named:/named", result);
        Assert.Contains("- /abs:/abs", result);
        Assert.Contains("source: /opt/stacks/shop/conf", result);
        Assert.Contains("source: vol", result);
        Assert.Contains("build: https://github.com/x/y.git", result);
        Assert.Contains("file: /opt/stacks/shop/site.conf", result);
        Assert.Null(MatDock.Core.Stacks.ComposeCheck.Problem(result));
    }

    [Theory]
    [InlineData("/opt/stacks", null)]
    [InlineData("/", "absolute")]
    [InlineData("opt/stacks", "absolute")]
    [InlineData("/opt/../etc", "..")]
    public void Only_an_absolute_folder_below_the_root_is_scanned(string folder, string? problem)
    {
        var found = StackService.ImportFolderProblem(folder);
        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(problem, found);
        }
    }

    [Fact]
    public async Task A_scan_finds_the_compose_files_and_tells_what_runs_and_what_is_managed()
    {
        _site.App.Db(db =>
        {
            db.Stacks.Add(new MatDock.Core.Entities.Stack { Name = "managed", EnvironmentId = _site.EnvId, ComposeYaml = "services: {}\n" });
            return db.SaveChanges();
        });

        using var scope = _site.App.Services.CreateScope();
        var env = _site.App.Db(db => db.Environments.Single(e => e.Id == _site.EnvId));
        var (found, error) = await scope.ServiceProvider.GetRequiredService<StackService>().ScanFolderAsync(env, "/opt/stacks/");

        Assert.Null(error);
        Assert.Equal(new[] { "blog", "managed", "running", "shop" }, found.Select(f => f.Name));
        Assert.True(found.Single(f => f.Name == "running").Running);
        Assert.True(found.Single(f => f.Name == "managed").Managed);
        Assert.Equal("/opt/stacks/shop/compose.yml", found.Single(f => f.Name == "shop").Path);
        Assert.Equal("/opt/stacks/blog", found.Single(f => f.Name == "blog").Folder);
    }

    [Fact]
    public async Task The_page_scans_and_imports_with_paths_and_env()
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        var html = await (await browser.GetAsync("/Stacks/Import")).Content.ReadAsStringAsync();

        var scan = await browser.PostAsync("/Stacks/Import?handler=Scan", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["EnvironmentId"] = _site.EnvId.ToString(),
            ["Folder"] = "/opt/stacks",
        }));
        var list = await scan.Content.ReadAsStringAsync();
        Assert.Contains("/opt/stacks/shop/compose.yml", list);
        Assert.DoesNotContain("node_modules", list);
        Assert.DoesNotContain("compose.override.yml", list);
        Assert.DoesNotContain("config.yml", list);

        var import = await browser.PostAsync("/Stacks/Import?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(list),
            ["EnvironmentId"] = _site.EnvId.ToString(),
            ["Folder"] = "/opt/stacks",
            ["Rows[0].Path"] = "/opt/stacks/shop/compose.yml",
            ["Rows[0].Name"] = "shop-imported",
            ["Rows[0].Selected"] = "true",
            ["Rows[1].Path"] = "/opt/stacks/blog/docker-compose.yaml",
            ["Rows[1].Name"] = "blog-imported",
            ["Rows[1].Selected"] = "true",
            ["Rows[2].Path"] = "/opt/stacks/notes/compose.yml",
            ["Rows[2].Name"] = "not-chosen",
        }));
        var result = await import.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Contains("&#x27;shop-imported&#x27; imported", result);

        var shop = _site.App.Db(db => db.Stacks.Single(s => s.Name == "shop-imported"));
        Assert.Contains("- /opt/stacks/shop/data:/data", shop.ComposeYaml);
        Assert.Contains("build: /opt/stacks/shop", shop.ComposeYaml);
        Assert.Contains("env_file: /opt/stacks/shop/app.env", shop.ComposeYaml);
        Assert.Contains("- cache:/cache", shop.ComposeYaml);
        Assert.Equal("TZ=Europe/Berlin\n", shop.EnvContent);
        Assert.Null(_site.App.Db(db => db.Stacks.Single(s => s.Name == "blog-imported").EnvContent));
        Assert.False(_site.App.Db(db => db.Stacks.Any(s => s.Name == "not-chosen")));
    }
}
