using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using MatDock.Core.Entities;
using MatDock.Core.Schedules;
using MatDock.Core.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// The sync of a Git stack against a real repository on disk: no new commit does nothing, a commit outside the compose folder does nothing, a change in
/// it deploys; the push webhook answers "skipped" and checks signatures; the stack's own scheduled task follows its auto-sync setting.
/// </summary>
public sealed class GitSyncTests : IClassFixture<GitSyncTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("gitsync-host");
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public GitSyncTests(Site site) => _site = site;

    private static readonly LibGit2Sharp.Signature Author = new("Test", "test@example.invalid", DateTimeOffset.UnixEpoch);

    /// <summary>A repository with <c>deploy/compose.yml</c> and a README beside the folder.</summary>
    private static string NewRepository()
    {
        var dir = Directory.CreateTempSubdirectory("matdock-gitsync").FullName;
        Repository.Init(dir);
        Directory.CreateDirectory(Path.Combine(dir, "deploy"));
        File.WriteAllText(Path.Combine(dir, "deploy", "compose.yml"), "services:\n  web:\n    image: nginx:1.27\n");
        File.WriteAllText(Path.Combine(dir, "README.md"), "first\n");
        Commit(dir, "first");
        return dir;
    }

    private static string Commit(string dir, string message)
    {
        using var repo = new Repository(dir);
        Commands.Stage(repo, "*");
        return repo.Commit(message, Author, Author).Sha;
    }

    private long AddGitStack(string name, string repo)
        => _site.App.Db(db =>
        {
            var stack = new Stack { Name = name, EnvironmentId = _site.EnvId, GitRepoUrl = repo, GitComposePath = "deploy/compose.yml", WebhookToken = "tok-0123456789abcdef-" + name };
            db.Stacks.Add(stack);
            db.SaveChanges();
            return stack.Id;
        });

    private async Task<GitSyncResult> SyncAsync(long id, bool force = false)
    {
        using var scope = _site.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StackService>().SyncGitAsync(id, new DeployRequest(Trigger: DeployTrigger.Sync, Actor: "test"), force);
    }

    [Fact]
    public async Task A_sync_deploys_only_when_the_compose_folder_changed()
    {
        var repo = NewRepository();
        try
        {
            var id = AddGitStack("sync-folder", repo);

            var first = await SyncAsync(id);
            Assert.Equal(GitSyncOutcome.Deployed, first.Outcome);
            var stack = _site.App.Db(db => db.Stacks.Single(s => s.Id == id));
            Assert.Equal(first.Commit, stack.GitLastCommit);
            Assert.NotNull(stack.GitTreeHash);

            var again = await SyncAsync(id);
            Assert.Equal(GitSyncOutcome.Skipped, again.Outcome);
            Assert.Contains("No new commits", again.Message);

            File.WriteAllText(Path.Combine(repo, "README.md"), "second\n");
            var outside = Commit(repo, "readme only");
            var readme = await SyncAsync(id);
            Assert.Equal(GitSyncOutcome.Skipped, readme.Outcome);
            Assert.Contains("nothing changed", readme.Message);
            Assert.Equal(outside, _site.App.Db(db => db.Stacks.Single(s => s.Id == id).GitLastCommit));

            File.WriteAllText(Path.Combine(repo, "deploy", "compose.yml"), "services:\n  web:\n    image: nginx:1.28\n");
            var inside = Commit(repo, "new image");
            var changed = await SyncAsync(id);
            Assert.Equal(GitSyncOutcome.Deployed, changed.Outcome);
            Assert.Equal(inside, changed.Commit);

            Assert.Equal(GitSyncOutcome.Deployed, (await SyncAsync(id, force: true)).Outcome);

            var deploys = _site.App.Db(db => db.StackDeployments.Where(d => d.StackId == id).ToList());
            Assert.Equal(3, deploys.Count);
            Assert.All(deploys, d => Assert.Equal(DeployTrigger.Sync, d.Trigger));
            Assert.Contains(deploys, d => d.CommitSha == inside);
        }
        finally
        {
            GitRepoCleanup(repo);
        }
    }

    [Fact]
    public void The_tree_hash_covers_the_compose_folder_only()
    {
        var dir = Directory.CreateTempSubdirectory("matdock-treehash").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "deploy", ".config"));
            File.WriteAllText(Path.Combine(dir, "deploy", "compose.yml"), "a");
            File.WriteAllText(Path.Combine(dir, "other.txt"), "x");
            var before = StackService.ComposeTreeHash(dir, "deploy/compose.yml");

            File.WriteAllText(Path.Combine(dir, "other.txt"), "y");
            Assert.Equal(before, StackService.ComposeTreeHash(dir, "deploy/compose.yml"));

            File.WriteAllText(Path.Combine(dir, "deploy", ".config", "app.ini"), "z");
            Assert.NotEqual(before, StackService.ComposeTreeHash(dir, "deploy/compose.yml"));

            // A compose file at the root covers the whole tree.
            var root = StackService.ComposeTreeHash(dir, "compose.yml");
            File.WriteAllText(Path.Combine(dir, "other.txt"), "w");
            Assert.NotEqual(root, StackService.ComposeTreeHash(dir, "compose.yml"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_push_webhook_skips_without_a_new_commit_and_checks_signatures()
    {
        var repo = NewRepository();
        try
        {
            var id = AddGitStack("sync-hook", repo);
            await SyncAsync(id);
            var client = _site.App.Browser();

            var skipped = await client.PostAsync("/webhooks/stacks/tok-0123456789abcdef-sync-hook", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
            Assert.Equal("skipped", JsonDocument.Parse(await skipped.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

            var badSignature = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stacks/tok-0123456789abcdef-sync-hook") { Content = new StringContent("{\"ref\":\"x\"}") };
            badSignature.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(badSignature)).StatusCode);

            var badGitlab = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stacks/tok-0123456789abcdef-sync-hook") { Content = new StringContent("{}") };
            badGitlab.Headers.Add("X-Gitlab-Token", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(badGitlab)).StatusCode);

            const string body = "{\"ref\":\"refs/heads/main\"}";
            var signed = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stacks/tok-0123456789abcdef-sync-hook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            signed.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("tok-0123456789abcdef-sync-hook"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(signed)).StatusCode); // still no new commit: skipped

            File.WriteAllText(Path.Combine(repo, "deploy", "compose.yml"), "services:\n  web:\n    image: nginx:1.29\n");
            Commit(repo, "push");
            var started = await client.PostAsync("/webhooks/stacks/tok-0123456789abcdef-sync-hook", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var operationId = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("operationId").GetString()!;
            await _site.App.Services.GetRequiredService<MatDock.Core.Operations.OperationRegistry>().WaitAsync(operationId);
            Assert.Contains(_site.App.Db(db => db.StackDeployments.Where(d => d.StackId == id).ToList()), d => d.Trigger == DeployTrigger.Webhook);
        }
        finally
        {
            GitRepoCleanup(repo);
        }
    }

    [Fact]
    public async Task The_editor_keeps_the_stacks_sync_task_in_line_with_its_setting()
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        var html = await (await browser.GetAsync("/Stacks/Edit")).Content.ReadAsStringAsync();

        Dictionary<string, string> Form(string token, string sync, string? cron = null, long? id = null)
        {
            var form = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Name"] = "sync-task",
                ["Input.EnvironmentId"] = _site.EnvId.ToString(),
                ["Input.Source"] = "git",
                ["Input.GitRepoUrl"] = "https://git.example.invalid/shop.git",
                ["Input.GitSync"] = sync,
            };
            if (cron is not null) { form["Input.GitSyncCron"] = cron; }
            if (id is not null) { form["Input.Id"] = id.ToString()!; }
            return form;
        }

        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(Form(SignIn.AntiforgeryToken(html), "15")))).StatusCode);
        var stack = _site.App.Db(db => db.Stacks.Single(s => s.Name == "sync-task"));
        Assert.Equal("*/15 * * * *", stack.GitSyncCron);
        var task = _site.App.Db(db => db.ScheduledTasks.Single(t => t.SourceKind == GitSyncAction.Source && t.SourceId == stack.Id));
        Assert.Equal(ScheduleAction.GitSync, task.Action);
        Assert.Equal("*/15 * * * *", task.Cron);
        Assert.True(task.Enabled);

        var edit = await (await browser.GetAsync($"/Stacks/Edit?id={stack.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"15\" selected=\"selected\">", edit);

        var bad = await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(Form(SignIn.AntiforgeryToken(edit), "custom", "not a cron", stack.Id)));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); // the form again, with the error
        Assert.Contains("Invalid cron", await bad.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(Form(SignIn.AntiforgeryToken(edit), "off", null, stack.Id)))).StatusCode);
        Assert.False(_site.App.Db(db => db.ScheduledTasks.Any(t => t.SourceKind == GitSyncAction.Source && t.SourceId == stack.Id)));
    }

    [Fact]
    public async Task A_sync_task_without_its_stack_switches_itself_off()
    {
        var task = _site.App.Db(db =>
        {
            var t = new ScheduledTask { Name = "orphan", Action = ScheduleAction.GitSync, Cron = "*/5 * * * *", SourceKind = GitSyncAction.Source, SourceId = 987654, Enabled = true };
            db.ScheduledTasks.Add(t);
            db.SaveChanges();
            return t;
        });

        using (var scope = _site.App.Services.CreateScope())
        {
            var action = scope.ServiceProvider.GetServices<IScheduleAction>().Single(a => a.Type == ScheduleAction.GitSync);
            var (ok, summary) = await action.ExecuteAsync(task);
            Assert.False(ok);
            Assert.Contains("switched off", summary);
        }

        Assert.False(_site.App.Db(db => db.ScheduledTasks.Single(t => t.Id == task.Id).Enabled));
    }

    /// <summary>A clone's pack files are read-only on Windows: clear the flags before deleting.</summary>
    private static void GitRepoCleanup(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
    }
}
