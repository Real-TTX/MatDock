using System.Text.Json;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Updates;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// Compose projects MatDock did not deploy (discovered ones) under the update rules: a moved tag is updated in place with the project's own files by a
/// helper with the Docker CLI; one whose files are not on the host, or that compose started without files, is said once as available and not tried again.
/// </summary>
public class DiscoveredUpdatesTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    private const string Files = "com.docker.compose.project.working_dir=/opt/stacks/blog,com.docker.compose.project.config_files=/opt/stacks/blog/compose.yaml";

    /// <summary>A rig whose host runs only the discovered project <c>blog</c>, with a moved tag in the registry.</summary>
    private static UpdateRig Rig(string labels = Files)
    {
        var rig = new UpdateRig(compose: "services:\n  web:\n    image: localhost:5000/app:latest\n", runningImage: "localhost:5000/app:latest");
        // The fake Docker answers every project with all containers: without the managed stack only the discovered one is there.
        rig.Db.Context.Stacks.Remove(rig.Stack);
        rig.Db.Context.SaveChanges();
        rig.Hosts.Containers.Clear();
        rig.Hosts.Containers.Add(JsonSerializer.Serialize(new
        {
            ID = "b0000000001", Names = "blog-web-1", Image = "localhost:5000/app:latest", State = "running", Status = "Up 3 hours",
            Labels = "com.docker.compose.project=blog,com.docker.compose.service=web" + (labels.Length > 0 ? "," + labels : string.Empty), Ports = string.Empty,
        }));
        rig.Connection.Images = [new DockerImage { Id = "1", Repository = "localhost:5000/app", Tag = "latest", Digest = "sha256:" + new string('a', 64) }];
        rig.RegistryDigest = "sha256:" + new string('b', 64);
        return rig;
    }

    private static Task AutoAsync(UpdateRig rig)
        => rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Tag; p.PreBackup = false; p.Rollback = false; });

    private static List<string> Helpers(UpdateRig rig) => rig.Hosts.Commands.Where(c => c.Contains("'docker:cli'", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task A_moved_tag_is_updated_in_place_with_the_projects_own_files()
    {
        using var rig = Rig();
        await AutoAsync(rig);

        var report = await rig.RunAsync(T0);

        Assert.Equal(1, report.Applied);
        var helper = Assert.Single(Helpers(rig));
        Assert.Contains("--mount 'type=bind,src=/opt/stacks/blog,dst=/opt/stacks/blog,readonly'", helper);
        Assert.Contains("-v '/var/run/docker.sock:/var/run/docker.sock'", helper);
        Assert.Contains("--log-driver none", helper);
        Assert.Contains("compose -p", helper);
        Assert.Contains(" pull ", helper);
        Assert.Contains("up -d --no-deps", helper);
        Assert.Contains(rig.Notes.Sent, n => n.Subject == "Updated: blog on rig");
    }

    [Fact]
    public async Task Files_that_are_not_on_the_host_are_said_once_and_not_tried_again()
    {
        using var rig = Rig();
        await AutoAsync(rig);
        rig.Hosts.Fails = c => c.Contains("'docker:cli'", StringComparison.Ordinal);
        rig.Hosts.Respond = c => c.Contains("'docker:cli'", StringComparison.Ordinal)
            ? "docker: Error response from daemon: invalid mount config for type \"bind\": bind source path does not exist: /opt/stacks/blog"
            : null;

        var report = await rig.RunAsync(T0);

        Assert.Equal((0, 0), (report.Applied, report.Failed));
        Assert.Contains(report.Lines, l => l.Contains("not updated by MatDock", StringComparison.Ordinal) && l.Contains("not on the host", StringComparison.Ordinal));
        var note = Assert.Single(rig.Notes.Sent);
        Assert.Equal("Updates available that MatDock does not apply: rig", note.Subject);
        Assert.Contains("- blog: web — The compose files of this project are not on the host", note.Body);

        await rig.RunAsync(T0.AddHours(7));
        Assert.Single(Helpers(rig));
        Assert.Single(rig.Notes.Sent);
    }

    [Fact]
    public async Task A_project_started_without_compose_files_is_not_touched()
    {
        using var rig = Rig(labels: string.Empty);
        await AutoAsync(rig);

        var report = await rig.RunAsync(T0);

        Assert.Empty(Helpers(rig));
        Assert.Contains(report.Lines, l => l.Contains("Compose named no files", StringComparison.Ordinal));
        Assert.Contains(rig.Notes.Sent, n => n.Subject == "Updates available that MatDock does not apply: rig");
    }

    [Fact]
    public async Task Projects_MatDock_cannot_update_are_said_in_one_message_per_environment()
    {
        using var rig = Rig(labels: string.Empty);
        rig.Hosts.Containers.Add(JsonSerializer.Serialize(new
        {
            ID = "w0000000001", Names = "wiki-app-1", Image = "localhost:5000/app:latest", State = "running", Status = "Up 3 hours",
            Labels = "com.docker.compose.project=wiki,com.docker.compose.service=app", Ports = string.Empty,
        }));
        await AutoAsync(rig);

        await rig.RunAsync(T0);

        var note = Assert.Single(rig.Notes.Sent);
        Assert.Contains("- blog:", note.Body);
        Assert.Contains("- wiki:", note.Body);
    }

    [Fact]
    public async Task A_failed_update_is_reported_and_not_tried_again_for_the_same_image()
    {
        using var rig = Rig();
        await AutoAsync(rig);
        rig.Hosts.Fails = c => c.Contains("'docker:cli'", StringComparison.Ordinal);
        rig.Hosts.Respond = c => c.Contains("'docker:cli'", StringComparison.Ordinal) ? "Error response from daemon: manifest unknown" : null;

        var report = await rig.RunAsync(T0);

        Assert.Equal(1, report.Failed);
        Assert.Contains(rig.Notes.Sent, n => n.Subject == "Update failed: blog on rig" && n.Body.Contains("manifest unknown", StringComparison.Ordinal));
        await rig.RunAsync(T0.AddHours(7));
        Assert.Single(Helpers(rig));
    }

    [Fact]
    public async Task Notify_announces_once_and_changes_nothing()
    {
        using var rig = Rig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Tag; });

        var first = await rig.RunAsync(T0);
        await rig.RunAsync(T0.AddHours(7));

        Assert.Equal(1, first.Announced);
        Assert.Empty(Helpers(rig));
        Assert.Single(rig.Notes.Sent, n => n.Subject == "Update available: blog on rig");
    }

    [Fact]
    public async Task An_environment_that_is_off_is_not_looked_at()
    {
        using var rig = Rig();
        await AutoAsync(rig);
        await rig.Policies.SaveAsync(new UpdatePolicy { Scope = UpdatePolicyScope.Environment, ScopeId = rig.Env.Id, Mode = UpdateMode.Off });

        var report = await rig.RunAsync(T0);

        Assert.Equal(0, report.Looked);
        Assert.Empty(Helpers(rig));
        Assert.Equal(0, rig.RegistryCalls);
    }

    [Fact]
    public void The_files_come_from_the_labels_and_unsafe_paths_are_refused()
    {
        var files = ComposeProjectFiles.FromLabels(new Dictionary<string, string>
        {
            [ComposeProjectFiles.WorkingDirLabel] = "/srv/app/",
            [ComposeProjectFiles.ConfigFilesLabel] = "compose.yaml,/etc/app/override.yml",
            [ComposeProjectFiles.EnvFilesLabel] = ".env",
        })!;

        Assert.Equal("/srv/app", files.WorkingDir);
        Assert.Equal(["/srv/app/compose.yaml", "/etc/app/override.yml"], files.ConfigFiles);
        Assert.Equal(["/srv/app/.env"], files.EnvFiles);
        Assert.Equal(["/srv/app", "/etc/app"], files.Folders());

        static ComposeProjectFiles? Of(string wd, string? config = "compose.yaml")
        {
            var labels = new Dictionary<string, string> { [ComposeProjectFiles.WorkingDirLabel] = wd };
            if (config is not null)
            {
                labels[ComposeProjectFiles.ConfigFilesLabel] = config;
            }

            return ComposeProjectFiles.FromLabels(labels);
        }

        Assert.Null(Of("/srv/../etc"));
        Assert.Null(Of("/srv/my app"));
        Assert.Null(Of("/srv/a'b"));
        Assert.Null(Of("relative/dir"));
        Assert.Null(Of("/srv/app", config: null));
        Assert.Null(Of("/srv/app", config: "../../etc/x.yml"));
        Assert.Null(ComposeProjectFiles.FromLabels(new Dictionary<string, string>()));
    }

    [Fact]
    public void The_helper_reaches_Docker_through_its_socket_only()
    {
        var files = new ComposeProjectFiles("/opt/app", ["/opt/app/compose.yaml"], []);

        var rootless = ProjectUpdateCommands.Run(files, "app", ["web"], pull: true, "docker", "unix:///run/user/1000/docker.sock", "h")!;
        Assert.Contains("-v '/run/user/1000/docker.sock:/var/run/docker.sock'", rootless);
        Assert.Null(ProjectUpdateCommands.Run(files, "app", ["web"], pull: true, "docker", "tcp://10.0.0.1:2375", "h"));

        var rollback = ProjectUpdateCommands.Run(files, "app", ["web", "worker"], pull: false, "sudo docker", null, "h")!;
        Assert.DoesNotContain(" pull ", rollback);
        Assert.Contains("sudo docker run --rm", rollback);
        Assert.Contains("up -d --no-deps", rollback);

        Assert.Throws<ArgumentException>(() => ProjectUpdateCommands.Run(files, "app", ["web;rm -rf /"], pull: true, "docker", null, "h"));
        Assert.Throws<ArgumentException>(() => ProjectUpdateCommands.Run(files, "app", [], pull: true, "docker", null, "h"));
        Assert.StartsWith("matdock-update-app-", ProjectUpdateCommands.HelperName("app"));
    }
}
