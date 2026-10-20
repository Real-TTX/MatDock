using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Web.Support;

namespace MatDock.Tests.Platform;

/// <summary>
/// The pages as the MatUi toolkit wants them (like the other engines): rendered markup without inline style, script or handlers,
/// icons only from the toolkit's sprite (inlined once), a label on every table cell for the phone card, a title on every page,
/// and the accent that MatDesign's registry gives MatDock.
/// </summary>
public sealed partial class UiTests : IClassFixture<UiTests.Site>
{
    /// <summary>An app of its own with a host, containers, images, a schedule and backups, so the lists render rows.</summary>
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("ui-host");
            App.Db(db =>
            {
                db.ScheduledTasks.Add(new ScheduledTask
                {
                    Name = "Nightly", Action = ScheduleAction.BundleBackup, Trigger = ScheduleTrigger.Cron, Cron = "0 3 * * *", Enabled = true,
                    LastRunAt = DateTime.UtcNow.AddHours(-2), LastRunOk = true, NextRunAt = DateTime.UtcNow.AddHours(5),
                });
                db.StackBackups.Add(new StackBackup { EnvironmentId = EnvId, EnvironmentName = "ui-host", StackName = "web", Status = StackBackupStatus.Succeeded, Message = "2 volumes" });
                db.VolumeBackups.Add(new VolumeBackup { VolumeName = "web_data", FileName = "a.tar.gz", SizeBytes = 1000 });
                return db.SaveChanges();
            });
            App.Hosts.Containers.AddRange(
            [
                Container("a", "running", "Up 3 hours", "web"),
                Container("b", "exited", "Exited (0) 2 days ago", "db"),
                Container("c", "running", "Up 1 minute", null),
            ]);
            App.Connection.Images = [new DockerImage { Id = "1", Repository = "nginx", Tag = "1" }, new DockerImage { Id = "2", Repository = "<none>", Tag = "<none>" }];
        }

        public MatDockFactory App { get; }

        public long EnvId { get; }

        public void Dispose() => App.Dispose();

        private static string Container(string id, string state, string status, string? project)
            => JsonSerializer.Serialize(new
            {
                ID = id,
                Names = "c-" + id,
                Image = "nginx",
                State = state,
                Status = status,
                Labels = project is null ? string.Empty : $"com.docker.compose.project={project},com.docker.compose.service=app",
                Ports = "0.0.0.0:8080->80/tcp",
            });
    }

    private readonly Site _site;

    public UiTests(Site site) => _site = site;

    /// <summary>Every page of the menu and its editors (the ones that need an id get the host of the fixture).</summary>
    public static TheoryData<string> Pages() =>
    [
        "/", "/Environments", "/Environments?view=gallery", "/Environments/Edit", "/Environments/Migrate", "/Launchpad", "/Apps", "/Apps/Edit",
        "/Stacks", "/Stacks?view=list", "/Stacks/Edit", "/Stacks/SyncJobs", "/Stacks/SyncJobs/Edit",
        "/Containers", "/Containers?view=gallery",
        "/Images", "/Volumes", "/Volumes/Create", "/Volumes/Prune", "/Networks", "/Networks/Prune",
        "/Schedules", "/Schedules/Edit", "/Backups", "/Backups/Targets",
        "/Settings", "/Settings?tab=backup", "/Settings/Notifications", "/Settings/Activity", "/Settings/Proxies", "/Settings/Proxies/Edit",
        "/Settings/Registries", "/Settings/Registries/Edit", "/Settings/BackupTargetEdit",
        "/Users", "/Users/Edit", "/GitCredentials", "/GitCredentials/Edit", "/GitRepos", "/GitRepos/Edit", "/Account/Profile",
    ];

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Account/Login", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        return browser;
    }

    private async Task<string> PageAsync(HttpClient browser, string path)
    {
        var response = await browser.GetAsync(path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_page_keeps_to_the_rules_of_the_toolkit(string path)
    {
        var html = await PageAsync(await AdminAsync(), path);
        AssertToolkitRules(path, html);
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/AccessDenied")]
    [InlineData("/Error")]
    public async Task A_page_without_the_menu_keeps_to_the_rules_too(string path)
    {
        var html = await PageAsync(_site.App.Browser(), path);
        AssertToolkitRules(path, html);
        Assert.Contains("class=\"mat-auth\"", html);
    }

    private static void AssertToolkitRules(string path, string html)
    {
        Assert.True(TitleRegex().IsMatch(html), $"{path} has no title");
        Assert.False(InlineStyleRegex().IsMatch(html), $"{path} has an inline style: {InlineStyleRegex().Match(html).Value}");
        Assert.False(InlineScriptRegex().IsMatch(html), $"{path} has an inline script");
        Assert.False(InlineHandlerRegex().IsMatch(html), $"{path} has an inline event handler: {InlineHandlerRegex().Match(html).Value}");
        Assert.Single(Regex.Matches(html, "class=\"mat-sprite\""));
        Assert.Contains($"data-app=\"{Ui.App.Id}\"", html);

        var sprite = Sprite();
        var missing = IconUseRegex().Matches(html).Select(m => m.Groups[1].Value).Distinct().Where(name => !sprite.Contains(name)).ToList();
        Assert.True(missing.Count == 0, $"{path} uses icons the toolkit does not have: {string.Join(", ", missing)}");

        foreach (Match cell in CellRegex().Matches(html))
        {
            var tag = cell.Value;
            Assert.True(tag.Contains("data-label=") || tag.Contains("mat-table__cell-actions") || tag.Contains("colspan="), $"{path}: a cell without data-label: {tag}");
        }
    }

    [Fact]
    public void The_registered_accent_is_the_one_the_toolkit_gives_the_app()
    {
        var css = File.ReadAllText(Path.Combine(WebRoot(), "matui", "matui.css"));
        var accent = Regex.Match(css, $"\\[data-app=\"{Ui.App.Id}\"\\]\\s*{{\\s*--mat-app-accent:\\s*(#[0-9a-fA-F]{{6}})").Groups[1].Value;
        Assert.Equal(Ui.App.Accent, accent, ignoreCase: true);

        var manifest = File.ReadAllText(Path.Combine(WebRoot(), "manifest.webmanifest"));
        Assert.Contains($"\"theme_color\": \"{Ui.App.Accent}\"", manifest);
    }

    [Fact]
    public void The_own_styles_only_use_the_tokens_of_the_toolkit()
    {
        var css = File.ReadAllText(Path.Combine(WebRoot(), "css", "matdock.css"));
        var colours = Regex.Matches(css, @"#[0-9a-fA-F]{3,8}\b|rgba?\(").Select(m => m.Value).ToList();
        Assert.True(colours.Count == 0, $"matdock.css has colours of its own: {string.Join(", ", colours)}");
        var variables = Regex.Matches(css, @"var\(--(?!mat-)([a-z0-9-]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(variables.Count == 0, $"matdock.css uses variables that are not the toolkit's: {string.Join(", ", variables)}");
    }

    private static HashSet<string> Sprite()
        => SymbolRegex().Matches(File.ReadAllText(Path.Combine(WebRoot(), "matui", "matui-icons.svg"))).Select(m => m.Groups[1].Value).ToHashSet();

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatDock.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("MatDock.slnx not found"), "src", "MatDock.Web", "wwwroot");
    }

    [GeneratedRegex("<title>[^<]+</title>")]
    private static partial Regex TitleRegex();

    [GeneratedRegex("#mat-i-([a-z0-9-]+)")]
    private static partial Regex IconUseRegex();

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"<[a-z][^>]*\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyleRegex();

    [GeneratedRegex(@"<script(?![^>]*\ssrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerRegex();

    // an empty cell (a spacer) has nothing to label
    [GeneratedRegex(@"<td\b[^>]*>(?!</td>)", RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();
}
