using System.Formats.Tar;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>MatDock may run another image than the compose file names, without touching the file: the choice per service, what happens when the
/// compose file is edited afterwards, and how it reaches compose on the host.</summary>
public class ImageOverridesTests
{
    private const string Compose = "services:\n  web:\n    image: nginx:latest\n  db:\n    image: postgres:16\n  worker:\n    build: .\n";

    private static IReadOnlyDictionary<string, ImageOverride> One(string service, string baseImage, string image)
        => new Dictionary<string, ImageOverride> { [service] = new(baseImage, image) };

    // ---- the compose file ----

    [Fact]
    public void The_images_of_a_compose_file_are_read_as_written()
    {
        var images = ImageOverrides.ComposeImages("services:\n  web:\n    image: ${REGISTRY:-ghcr.io}/app:${TAG:-latest}\n  db:\n    image: postgres:16\n  job:\n    build: .\n");
        Assert.Equal("${REGISTRY:-ghcr.io}/app:${TAG:-latest}", images["web"]);
        Assert.Equal("postgres:16", images["db"]);
        Assert.False(images.ContainsKey("job")); // a build has no image to override
    }

    [Theory]
    [InlineData("")]
    [InlineData("services: [unclosed")]
    [InlineData("include:\n  - other.yml\n")]
    public void Text_that_has_no_services_says_nothing(string yaml)
        => Assert.False(ImageOverrides.TryComposeImages(yaml, out _));

    // ---- when the compose file is edited ----

    [Fact]
    public void An_override_stays_while_the_compose_names_the_same_image()
    {
        var kept = ImageOverrides.Reconcile(One("web", "nginx:latest", "nginx:1.27.3"), Compose.Replace("postgres:16", "postgres:17"), out var notes);

        Assert.Equal("nginx:1.27.3", kept["web"].Image);
        Assert.Empty(notes); // another service changed: no business of web's
    }

    [Fact]
    public void An_edited_image_line_wins_over_the_override_and_says_so()
    {
        var kept = ImageOverrides.Reconcile(One("web", "nginx:latest", "nginx:1.27.3"), Compose.Replace("nginx:latest", "nginx:1.28"), out var notes);

        Assert.Empty(kept);
        var note = Assert.Single(notes);
        Assert.Contains("web", note);
        Assert.Contains("nginx:1.28", note);
        Assert.Contains("nginx:latest", note);
    }

    [Fact]
    public void A_service_that_is_gone_loses_its_override()
    {
        var kept = ImageOverrides.Reconcile(One("cache", "redis:7", "redis:7.4"), Compose, out var notes);

        Assert.Empty(kept);
        Assert.Contains("cache", Assert.Single(notes));
    }

    [Theory]
    [InlineData("services: [unclosed")]
    [InlineData("include:\n  - other.yml\n")]
    public void A_half_typed_file_does_not_cost_the_overrides(string yaml)
    {
        var kept = ImageOverrides.Reconcile(One("web", "nginx:latest", "nginx:1.27.3"), yaml, out var notes);

        Assert.Equal("nginx:1.27.3", kept["web"].Image);
        Assert.Empty(notes);
    }

    // ---- the file for compose ----

    [Fact]
    public void The_override_file_only_sets_images_and_quotes_what_it_writes()
    {
        var yaml = ImageOverrides.Render(new Dictionary<string, ImageOverride>
        {
            ["web"] = new("nginx:latest", "nginx:1.27.3"),
            ["yes"] = new("a", "ghcr.io/o/app@sha256:0123456789abcdef"), // a service called yes must stay a string
        })!;

        Assert.Contains("services:\n  \"web\":\n    image: \"nginx:1.27.3\"\n", yaml);
        Assert.Contains("  \"yes\":\n    image: \"ghcr.io/o/app@sha256:0123456789abcdef\"\n", yaml);
        Assert.Null(ImageOverrides.Render(new Dictionary<string, ImageOverride>())); // nothing to override = no file
    }

    [Fact]
    public void A_stored_value_that_is_damaged_is_ignored_entry_by_entry()
    {
        var stack = new Stack
        {
            ImageOverridesJson = "{\"web\":{\"base\":\"nginx\",\"image\":\"nginx:1.27\"},\"bad name\":{\"base\":\"x\",\"image\":\"y\"},\"db\":{\"base\":\"p\",\"image\":\"a b;rm -rf /\"}}",
        };

        var overrides = ImageOverrides.Of(stack);
        Assert.Equal(new[] { "web" }, overrides.Keys);
        Assert.Empty(ImageOverrides.Parse("not json"));
        Assert.Empty(ImageOverrides.Parse(null));
    }

    [Fact]
    public void The_override_travels_to_the_host_on_stdin_beside_the_compose_file()
    {
        var yaml = ImageOverrides.Render(One("web", "nginx:latest", "nginx:1.27.3"));
        var lines = System.Text.Encoding.UTF8.GetString(StackCommands.FilesOnStdin("services: {}\n", null, yaml)).Split('\n');

        Assert.Equal(string.Empty, lines[0]); // no .env
        Assert.Equal(yaml, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[1])));
        Assert.Equal("services: {}", lines[2]);
    }

    // ---- the stack ----

    private static StackService Service(TestDatabase db)
        => new(db.Context, new NoHostSessionFactory(), null!, null!, null!, null!, new NoopScheduleEventBus(),
            Options.Create(new MatDockOptions()), NullLogger<StackService>.Instance);

    private static async Task<long> AddStackAsync(TestDatabase db, string compose = Compose)
    {
        var stack = new Stack { Name = "media", EnvironmentId = 1, ComposeYaml = compose };
        db.Context.Stacks.Add(stack);
        await db.Context.SaveChangesAsync();
        return stack.Id;
    }

    [Fact]
    public async Task An_override_is_remembered_and_the_compose_file_stays_as_it_is()
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);
        var svc = Service(db);

        var set = await svc.SetImageOverrideAsync(id, "web", "nginx:1.27.3");
        Assert.True(set.Ok, set.Message);

        var stack = (await svc.GetAsync(id))!;
        Assert.Equal(Compose, stack.ComposeYaml);
        var entry = ImageOverrides.Of(stack)["web"];
        Assert.Equal(("nginx:latest", "nginx:1.27.3"), (entry.Base, entry.Image));
    }

    [Fact]
    public async Task The_compose_image_or_nothing_takes_the_override_away()
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);
        var svc = Service(db);

        await svc.SetImageOverrideAsync(id, "web", "nginx:1.27.3");
        Assert.True((await svc.SetImageOverrideAsync(id, "web", "nginx:latest")).Ok); // the compose's own image
        Assert.Null((await svc.GetAsync(id))!.ImageOverridesJson);

        await svc.SetImageOverrideAsync(id, "db", "postgres:17");
        Assert.True((await svc.SetImageOverrideAsync(id, "db", null)).Ok);
        Assert.Null((await svc.GetAsync(id))!.ImageOverridesJson);
    }

    [Theory]
    [InlineData("web", "nginx 1.27")]
    [InlineData("web", "nginx;id")]
    [InlineData("bad name", "nginx:1")]
    [InlineData("worker", "busybox")] // built, not pulled: no image in the compose file to stand in for
    [InlineData("nope", "busybox")]
    public async Task A_bad_service_or_image_is_refused(string service, string image)
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);

        Assert.False((await Service(db).SetImageOverrideAsync(id, service, image)).Ok);
        Assert.Null((await Service(db).GetAsync(id))!.ImageOverridesJson);
    }

    [Fact]
    public async Task Editing_the_image_line_in_the_editor_drops_the_override_and_other_edits_do_not()
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);
        var svc = Service(db);
        await svc.SetImageOverrideAsync(id, "web", "nginx:1.27.3");

        StackInput Edit(string compose) => new() { Name = "media", EnvironmentId = 1, ComposeYaml = compose };

        // An edit elsewhere (db) keeps web's override.
        var other = await svc.UpdateAsync(id, Edit(Compose.Replace("postgres:16", "postgres:17")));
        Assert.True(other.Ok);
        Assert.DoesNotContain("override", other.Message);
        Assert.Equal("nginx:1.27.3", ImageOverrides.Of((await svc.GetAsync(id))!)["web"].Image);

        // An edit of web's own image line is a decision: it wins, and the message says what happened.
        var line = await svc.UpdateAsync(id, Edit(Compose.Replace("nginx:latest", "nginx:1.28")));
        Assert.True(line.Ok);
        Assert.Contains("override dropped", line.Message);
        Assert.Empty(ImageOverrides.Of((await svc.GetAsync(id))!));
    }

    [Fact]
    public async Task A_restored_backup_brings_back_the_overrides_that_fit_its_compose_file()
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);
        var svc = Service(db);
        var stored = ImageOverrides.Serialize(new Dictionary<string, ImageOverride>
        {
            ["web"] = new("nginx:latest", "nginx:1.27.3"),   // fits the restored compose
            ["db"] = new("postgres:15", "postgres:15.4"),     // was made against another line than the restored compose names
            ["cache"] = new("redis:7", "redis:7.4"),          // no such service
        });

        await svc.RestoreImageOverridesAsync(id, stored);

        var overrides = ImageOverrides.Of((await svc.GetAsync(id))!);
        Assert.Equal(new[] { "web" }, overrides.Keys);
        Assert.Equal("nginx:1.27.3", overrides["web"].Image);

        await svc.RestoreImageOverridesAsync(id, string.Empty); // a backup from before any override: none
        Assert.Null((await svc.GetAsync(id))!.ImageOverridesJson);
    }

    // ---- on the host (a real shell, with `echo` for docker: what compose would be asked is printed) ----

    private static (int Exit, string Output) RunCommand(string command, string stacksDir, byte[] stdin)
    {
        // The command is `sh -c <quoted script> [args] 2>&1`; run it as a whole through the shell.
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        psi.Environment["MATDOCK_STACKS_DIR"] = stacksDir;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.BaseStream.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, output);
    }

    [Fact]
    public void Deploy_and_Update_hand_compose_the_override_file_only_when_there_is_one()
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("matdock-override").FullName;
        try
        {
            var yaml = ImageOverrides.Render(One("web", "nginx:latest", "nginx:1.27.3"));
            foreach (var build in new Func<string>[] { () => StackCommands.Deploy("echo", "media"), () => StackCommands.Update("echo", "media") })
            {
                var with = RunCommand(build(), dir, StackCommands.FilesOnStdin(Compose, null, yaml));
                Assert.Equal(0, with.Exit);
                Assert.Contains("compose -p media -f docker-compose.yml -f matdock-override.yml up -d", with.Output);
                Assert.Equal(yaml, File.ReadAllText(Path.Combine(dir, "media", "matdock-override.yml")));

                // Without it the stale file is gone and compose is asked the plain way.
                var without = RunCommand(build(), dir, StackCommands.FilesOnStdin(Compose, null));
                Assert.Equal(0, without.Exit);
                Assert.DoesNotContain("matdock-override", without.Output);
                Assert.False(File.Exists(Path.Combine(dir, "media", "matdock-override.yml")));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_git_sync_writes_the_override_beside_the_repo_files()
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("matdock-override-git").FullName;
        var repo = Directory.CreateTempSubdirectory("matdock-override-repo").FullName;
        try
        {
            File.WriteAllText(Path.Combine(repo, "docker-compose.yml"), Compose);
            var tar = new MemoryStream();
            TarFile.CreateFromDirectory(repo, tar, includeBaseDirectory: false);

            var yaml = ImageOverrides.Render(One("web", "nginx:latest", "nginx:1.27.3"));
            var with = RunCommand(StackCommands.GitSync("echo", "media", "docker-compose.yml", pull: false, null, yaml), dir, tar.ToArray());
            Assert.Equal(0, with.Exit);
            Assert.Contains("compose -p media -f docker-compose.yml -f matdock-override.yml up -d", with.Output);
            Assert.Equal(yaml, File.ReadAllText(Path.Combine(dir, "media", "matdock-override.yml")));

            var without = RunCommand(StackCommands.GitSync("echo", "media", "docker-compose.yml", pull: true), dir, tar.ToArray());
            Assert.Equal(0, without.Exit);
            Assert.DoesNotContain("matdock-override", without.Output);
            Assert.False(File.Exists(Path.Combine(dir, "media", "matdock-override.yml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Stop_start_restart_pull_and_down_use_the_override_file_when_it_is_there()
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("matdock-override-life").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "media"));
            File.WriteAllText(Path.Combine(dir, "media", "docker-compose.yml"), Compose);
            var lifecycle = new Func<string>[]
            {
                () => StackCommands.Stop("echo", "media", "docker-compose.yml"),
                () => StackCommands.Start("echo", "media", "docker-compose.yml"),
                () => StackCommands.Restart("echo", "media", "docker-compose.yml"),
                () => StackCommands.Pull("echo", "media", "docker-compose.yml"),
                () => StackCommands.Down("echo", "media", "docker-compose.yml"),
            };

            foreach (var build in lifecycle)
            {
                Assert.DoesNotContain("matdock-override", RunCommand(build(), dir, []).Output);
            }

            File.WriteAllText(Path.Combine(dir, "media", "matdock-override.yml"), "services: {}\n");
            foreach (var build in lifecycle)
            {
                Assert.Contains("-f docker-compose.yml -f matdock-override.yml", RunCommand(build(), dir, []).Output);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
