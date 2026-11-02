using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Tests.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// MatDock manages the stack it runs in: a deploy, an update and a restart hand their last step to a helper container (done the ordinary way, compose
/// would stop the process that runs it and MatDock would stay down), and a stop, a down or a removal is refused.
/// </summary>
public class SelfUpdateTests
{
    private const string SelfId = "d96257a67c8d4f1e9b2a3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f7a8b";

    /// <summary>The real services over a test database; Docker is <see cref="FakeHostSessionFactory"/>, MatDock's container is in the stack "matdock".</summary>
    private sealed class Rig : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public FakeHostSessionFactory Hosts { get; } = new();
        public SelfContainer Self { get; } = new(SelfId);
        public StackService Stacks { get; }
        public ContainerService Containers { get; }
        public UpdateService Updates { get; }
        public DockerEnvironment Env { get; }
        public Stack Own { get; }
        public Stack Other { get; }

        /// <summary>What compose says on the host for a handed-over step.</summary>
        public string HelperResult { get; set; } = "0\n Container matdock-web-1  Recreated\n Container matdock-web-1  Started";

        public Rig(ConnectionType type = ConnectionType.Local)
        {
            var options = Options.Create(new MatDockOptions());
            var secrets = new FakeSecretProtector();
            var connection = new FakeConnectionService();
            var environments = new EnvironmentService(Db.Context, secrets, connection, options);
            var registries = new MatDock.Core.Registries.RegistryService(Db.Context, secrets,
                new MatDock.Core.Registries.RegistryApiClient(new TestHttpFactory(), NullLogger<MatDock.Core.Registries.RegistryApiClient>.Instance));
            Stacks = new StackService(Db.Context, Hosts, environments, new MatDock.Core.Git.GitCredentialService(Db.Context, secrets),
                new MatDock.Core.Git.GitRepositoryService(NullLogger<MatDock.Core.Git.GitRepositoryService>.Instance), registries,
                new NoopScheduleEventBus(), options, NullLogger<StackService>.Instance, new DeploymentHistory(Db.Context, NullLogger<DeploymentHistory>.Instance), Self);
            Containers = new ContainerService(Hosts, environments, options, NullLogger<ContainerService>.Instance, Self);
            Updates = new UpdateService(Containers, connection, environments, registries, Stacks, null!, new UpdateCheckCache(), Db.Context, NullLogger<UpdateService>.Instance);

            Env = new DockerEnvironment { Name = "home", ConnectionType = type, Host = "nas", Username = "me", Status = EnvironmentStatus.Online, IsEnabled = true };
            Db.Context.Environments.Add(Env);
            Db.Context.SaveChanges();
            Own = new Stack { Name = "matdock", EnvironmentId = Env.Id, ComposeYaml = "services:\n  web:\n    image: ghcr.io/me/matdock:latest\n" };
            Other = new Stack { Name = "shop", EnvironmentId = Env.Id, ComposeYaml = "services:\n  web:\n    image: nginx:1\n" };
            Db.Context.Stacks.AddRange(Own, Other);
            Db.Context.SaveChanges();

            Hosts.Containers.Add(JsonSerializer.Serialize(new
            {
                ID = SelfId[..12], Names = "matdock-web-1", Image = "ghcr.io/me/matdock:latest", State = "running", Status = "Up 3 hours",
                Labels = "com.docker.compose.project=matdock,com.docker.compose.service=web", Ports = string.Empty,
            }));
            Hosts.Respond = command =>
                command.Contains(" inspect -f '{{index .Config.Labels", StringComparison.Ordinal) ? "matdock|web"
                : command.Contains("else exit 3; fi", StringComparison.Ordinal) ? HelperResult // what the helper left beside the files
                : command.Contains("--name " + SelfHandover.HelperName, StringComparison.Ordinal) ? SelfHandover.Marker + ": a helper container runs docker compose up -d in 5 s."
                : null;
        }

        public int Count(string fragment) => Hosts.Commands.Count(c => c.Contains(fragment, StringComparison.Ordinal));

        public void Dispose() => Db.Dispose();
    }

    private sealed class TestHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public void The_id_comes_from_the_files_Docker_mounts_into_every_container()
    {
        var mountinfo = string.Join('\n',
            "735 600 0:47 / / rw,relatime master:313 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/ABC",
            "748 735 0:52 / /data rw,relatime master:1 - ext4 /dev/sda1 rw",
            $"750 735 8:1 /var/lib/docker/containers/{SelfId}/resolv.conf /etc/resolv.conf rw,relatime - ext4 /dev/sda1 rw",
            $"751 735 8:1 /var/lib/docker/containers/{SelfId}/hostname /etc/hostname rw,relatime - ext4 /dev/sda1 rw");
        Assert.Equal(SelfId, SelfContainer.Detect(mountinfo));

        // Another container's folder mounted somewhere else (a backup of /var/lib/docker) is not MatDock's own.
        Assert.Null(SelfContainer.Detect($"760 735 8:1 /var/lib/docker/containers/{SelfId} /backup/c rw - ext4 /dev/sda1 rw"));
        Assert.Null(SelfContainer.Detect("735 600 0:47 / / rw - overlay overlay rw"));
        Assert.Null(SelfContainer.Detect(null));
    }

    [Fact]
    public void Its_own_id_matches_in_full_and_short_and_nothing_else_does()
    {
        var self = new SelfContainer(SelfId);
        Assert.True(self.Is(SelfId));
        Assert.True(self.Is(SelfId[..12]));
        Assert.True(self.Is(SelfId[..12].ToUpperInvariant()));
        Assert.False(self.Is(SelfId[..8]));
        Assert.False(self.Is("0123456789ab"));
        Assert.False(self.Is("matdock-web-1"));
        Assert.False(new SelfContainer(null).Is(SelfId));
    }

    [Fact]
    public void The_answer_of_docker_inspect_is_remembered_only_when_it_is_certain()
    {
        Assert.Equal(new SelfPlace("matdock", "web"), SelfContainer.ParsePlace(true, "matdock|web\n", out var certain));
        Assert.True(certain);
        Assert.Equal(new SelfPlace(string.Empty, string.Empty), SelfContainer.ParsePlace(true, "<no value>|<no value>", out _));

        Assert.Null(SelfContainer.ParsePlace(false, "Error: No such object: d96257a67c8d", out certain));
        Assert.True(certain); // not on this environment
        Assert.Null(SelfContainer.ParsePlace(false, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock.", out certain));
        Assert.False(certain); // ask again next time
    }

    [Fact]
    public void Locally_the_helper_takes_MatDocks_mounts_over_SSH_the_socket_of_the_host_and_over_a_Docker_API_there_is_none()
    {
        SshConnectionSettings Settings(ConnectionType type, string? dockerHost = null)
            => new() { ConnectionType = type, Host = "nas", Username = "me", DockerHost = dockerHost };

        var local = SelfHandover.For(Settings(ConnectionType.Local), SelfId)!;
        Assert.Equal("--volumes-from " + SelfId, local.DockerAccess);
        Assert.True(local.FilesInVolume);

        var ssh = SelfHandover.For(Settings(ConnectionType.Ssh), SelfId)!;
        Assert.Equal("-v /var/run/docker.sock:/var/run/docker.sock", ssh.DockerAccess);
        Assert.False(ssh.FilesInVolume);
        Assert.Contains(" -v \"$dir\":\"$dir\"", ssh.Compose("docker", "-p \"$1\" -f \"$2\" $ov $ef", "up -d"));

        Assert.Equal("-v /run/user/1000/docker.sock:/var/run/docker.sock",
            SelfHandover.For(Settings(ConnectionType.Ssh, "unix:///run/user/1000/docker.sock"), SelfId)!.DockerAccess);
        Assert.Null(SelfHandover.For(Settings(ConnectionType.Ssh, "tcp://10.0.0.2:2375"), SelfId));
        Assert.Null(SelfHandover.For(Settings(ConnectionType.Tcp), SelfId));
        Assert.Null(SelfHandover.For(Settings(ConnectionType.Local), "not-an-id"));
    }

    [Fact]
    public void The_handed_over_step_runs_compose_in_the_helper_with_the_secrets_named_only_and_starts_the_old_MatDock_when_it_fails()
    {
        var fragment = new SelfHandover(SelfId, "--volumes-from " + SelfId, true).Compose("docker", "-p \"$name\" $cf", "up -d --remove-orphans");

        // MatDock's image by the reference it was started from (after a pull the old one is dangling), its id only when that is gone
        Assert.Contains("img=$(docker inspect -f \"{{.Config.Image}}\" " + SelfId + "); docker image inspect \"$img\" > /dev/null 2>&1 || img=$(docker inspect -f \"{{.Image}}\" " + SelfId + ");", fragment);
        Assert.Contains("docker run -d --rm --name matdock-self-update --no-healthcheck --volumes-from " + SelfId + " $envargs --entrypoint sh \"$img\" -c ", fragment);
        // compose waits until this MatDock answered, writes what it said and its exit code beside the files; a failure starts the old container again
        Assert.Contains("sleep 5; cd \\\"\\$0\\\" && docker compose \\\"\\$@\\\" up -d --remove-orphans > .matdock-self-update.log 2>&1; rc=\\$?; ", fragment);
        Assert.Contains("[ \\$rc -eq 0 ] || docker start " + SelfId + " >> .matdock-self-update.log 2>&1; echo \\$rc > .matdock-self-update.exit", fragment);
        Assert.Contains("\"$dir\" -p \"$name\" $cf > /dev/null;", fragment);
        Assert.Contains("for kv in $xs; do envargs=\"$envargs -e ${kv%%=*}\"; done", fragment);
        Assert.DoesNotContain("'", fragment); // it sits inside the single-quoted script bodies
        Assert.EndsWith("echo \"" + SelfHandover.Marker + ": a helper container runs docker compose up -d --remove-orphans in 5 s.\"", fragment);

        Assert.Throws<ArgumentException>(() => new SelfHandover(SelfId, "--volumes-from " + SelfId, true).Compose("docker", "-p x", "up -d $(reboot)"));
    }

    /// <summary>
    /// In a real shell (dash on Linux; skipped without /bin/sh): the deploy script hands the helper the secrets by name and compose's words one by one,
    /// and the helper's own script, run with a docker that writes down what it is asked, leaves the output and the exit code beside the files and
    /// starts the old MatDock again when compose fails.
    /// </summary>
    [Fact]
    public void In_a_real_shell_the_helper_gets_the_right_words_and_leaves_its_result()
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("matdock-self").FullName;
        try
        {
            var stacks = Path.Combine(root, "stacks");
            var log = Path.Combine(root, "docker.log");
            // A docker that writes every call (one argument per line, a blank line after) and answers inspect with an image id; "compose" fails on demand.
            var fake = Path.Combine(root, "docker");
            File.WriteAllText(fake, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\" >> \"$FAKE_LOG\"; done; echo >> \"$FAKE_LOG\"\n"
                                    + "case \"$1\" in inspect) echo sha256:abc;; compose) echo \" Container matdock-web-1  Recreated\"; [ -z \"$FAKE_FAIL\" ];; esac\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var sleep = Path.Combine(root, "sleep"); // the helper waits for nothing here
            File.WriteAllText(sleep, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(sleep, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var handover = new SelfHandover(SelfId, "--volumes-from " + SelfId, FilesInVolume: true);
            var command = StackCommands.Deploy(fake, "matdock", new DeployOptions { RemoveOrphans = true }, null, handover);
            var (exit, output) = Run(command, stacks, log, StackCommands.FilesOnStdin("services: {}\n", null, null, "DB_PASSWORD=p a$s\nAPI_TOKEN=t0k\n"));

            Assert.Equal(0, exit);
            Assert.Contains(SelfHandover.Marker, output);
            var run = File.ReadAllText(log).Split("\n\n").Single(c => c.StartsWith("run\n", StringComparison.Ordinal)).Split('\n');
            Assert.Equal(new[] { "run", "-d", "--rm", "--name", "matdock-self-update", "--no-healthcheck", "--volumes-from", SelfId, "-e", "DB_PASSWORD", "-e", "API_TOKEN",
                "--entrypoint", "sh", "sha256:abc", "-c" }, run[..16]);
            var inner = run[16];
            Assert.Equal(new[] { Path.Combine(stacks, "matdock"), "-p", "matdock" }, run[17..]); // no override file: $cf is empty
            Assert.DoesNotContain("p a$s", File.ReadAllText(log)); // the values only by name

            // The helper's script itself: with $0 = the folder and compose's words after it.
            foreach (var fails in new[] { false, true })
            {
                File.Delete(log);
                var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
                foreach (var a in new[] { "-c", inner, Path.Combine(stacks, "matdock"), "-p", "matdock" })
                {
                    psi.ArgumentList.Add(a);
                }

                psi.Environment["PATH"] = root + ":" + Environment.GetEnvironmentVariable("PATH");
                psi.Environment["FAKE_LOG"] = log;
                psi.Environment["FAKE_FAIL"] = fails ? "1" : string.Empty;
                using (var p = System.Diagnostics.Process.Start(psi)!)
                {
                    p.WaitForExit(20_000);
                }

                var dir = Path.Combine(stacks, "matdock");
                Assert.Equal(fails ? "1" : "0", File.ReadAllText(Path.Combine(dir, SelfHandover.ExitFile)).Trim());
                Assert.Contains("Recreated", File.ReadAllText(Path.Combine(dir, SelfHandover.LogFile)));
                var calls = File.ReadAllText(log);
                Assert.Contains("compose\n-p\nmatdock\nup\n-d\n--remove-orphans\n", calls);
                Assert.Equal(fails, calls.Contains("start\n" + SelfId + "\n", StringComparison.Ordinal));
            }

            // What MatDock reads after its restart: the exit code first, then the output; both files are gone after.
            var (read, result) = Run(SelfHandover.ReadResult("matdock"), stacks, log, []);
            Assert.Equal(0, read);
            Assert.StartsWith("1\n", result);
            Assert.False(File.Exists(Path.Combine(stacks, "matdock", SelfHandover.ExitFile)));
            Assert.Equal(3, Run(SelfHandover.ReadResult("matdock"), stacks, log, []).Exit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static (int Exit, string Output) Run(string command, string stacksDir, string log, byte[] stdin)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            psi.Environment["MATDOCK_STACKS_DIR"] = stacksDir;
            psi.Environment["FAKE_LOG"] = log;
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardInput.BaseStream.Write(stdin);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }
    }

    [Fact]
    public async Task A_deploy_of_its_own_stack_hands_up_to_the_helper_and_stays_open_in_the_history_until_the_restart()
    {
        using var rig = new Rig();

        var (ok, output) = await rig.Stacks.DeployAsync(rig.Own.Id);

        Assert.True(ok);
        Assert.Contains(SelfHandover.Marker, output);
        var deploy = Assert.Single(rig.Hosts.Commands, c => c.Contains("cat > \"$dir/docker-compose.yml\"", StringComparison.Ordinal));
        Assert.Contains("--name matdock-self-update", deploy);
        Assert.DoesNotContain("docker compose -p \"$name\" $cf up -d", deploy); // not in this process
        var row = Assert.Single(rig.Db.Context.StackDeployments.AsNoTracking().ToList());
        Assert.Null(row.Ok);
        Assert.Null(row.FinishedAt);
        Assert.Equal(DeploymentHistory.HandedOverSummary, row.Summary);

        // After the restart: what the helper left completes the row.
        Assert.Equal(0, await rig.Stacks.CompleteHandoversAsync(TimeSpan.FromMinutes(30)));
        rig.Db.Context.ChangeTracker.Clear();
        row = Assert.Single(rig.Db.Context.StackDeployments.AsNoTracking().ToList());
        Assert.True(row.Ok);
        Assert.NotNull(row.FinishedAt);
        Assert.Equal("1 recreated.", row.Summary);
        Assert.Contains("--- helper container ---", row.Log);
        Assert.Contains(rig.Hosts.Commands, c => c.Contains("rm -f \"$dir/.matdock-self-update.exit\" \"$dir/.matdock-self-update.log\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_handed_over_deploy_waits_for_the_helper_and_one_that_never_answers_ends_as_failed()
    {
        using var rig = new Rig();
        await rig.Stacks.DeployAsync(rig.Own.Id);
        rig.Hosts.Fails = c => c.Contains("else exit 3; fi", StringComparison.Ordinal); // the helper has not finished yet

        Assert.Equal(1, await rig.Stacks.CompleteHandoversAsync(TimeSpan.FromMinutes(30)));
        Assert.Null(rig.Db.Context.StackDeployments.AsNoTracking().Single().Ok);

        Assert.Equal(0, await rig.Stacks.CompleteHandoversAsync(TimeSpan.Zero));
        var row = rig.Db.Context.StackDeployments.AsNoTracking().Single();
        Assert.False(row.Ok);
        Assert.Equal("The helper container left no result.", row.Summary);
    }

    [Fact]
    public async Task A_failed_helper_ends_the_deploy_as_failed_with_what_compose_said()
    {
        using var rig = new Rig { HelperResult = "1\nError response from daemon: driver failed programming external connectivity: port is already allocated" };
        await rig.Stacks.DeployAsync(rig.Own.Id);
        await rig.Stacks.CompleteHandoversAsync(TimeSpan.FromMinutes(30));

        var row = rig.Db.Context.StackDeployments.AsNoTracking().Single();
        Assert.False(row.Ok);
        Assert.Contains("port is already allocated", row.Summary);
    }

    [Fact]
    public async Task Another_stack_on_the_same_environment_deploys_as_ever_and_the_environment_is_asked_once()
    {
        using var rig = new Rig();

        await rig.Stacks.DeployAsync(rig.Other.Id);
        await rig.Stacks.DeployAsync(rig.Other.Id);
        await rig.Stacks.DeployAsync(rig.Own.Id);

        Assert.Equal(1, rig.Count("{{index .Config.Labels"));
        Assert.Equal(1, rig.Count("--name matdock-self-update"));
        Assert.Equal(2, rig.Count("docker compose -p \"$name\" $cf up -d"));
    }

    [Fact]
    public async Task Its_own_stack_is_not_stopped_or_taken_down_and_a_restart_goes_through_the_helper()
    {
        using var rig = new Rig();

        Assert.Equal((false, StackService.SelfRefusal), await rig.Stacks.StopAsync(rig.Own.Id));
        Assert.Equal((false, StackService.SelfRefusal), await rig.Stacks.DownAsync(rig.Own.Id, removeVolumes: true));
        Assert.Equal((false, StackService.SelfRefusal), await rig.Stacks.ProjectStopAsync(rig.Env, "matdock"));
        Assert.Equal((false, StackService.SelfServiceRefusal), await rig.Stacks.ServiceActionAsync(rig.Own.Id, "web", StackCommands.ServiceAction.Stop));
        Assert.DoesNotContain(rig.Hosts.Commands, c => c.Contains(" stop", StringComparison.Ordinal) || c.Contains(" down", StringComparison.Ordinal));

        var (restarted, said) = await rig.Stacks.RestartAsync(rig.Own.Id);
        Assert.True(restarted);
        Assert.Contains(SelfHandover.Marker, said);
        Assert.Contains(rig.Hosts.Commands, c => c.Contains("docker restart \\$(docker ps -q --filter label=com.docker.compose.project=matdock)", StringComparison.Ordinal));

        // A pull and a start leave MatDock running: as for any stack.
        Assert.True((await rig.Stacks.PullAsync(rig.Own.Id)).Ok);
        Assert.Contains(rig.Hosts.Commands, c => c.Contains("$ov $ef pull", StringComparison.Ordinal));

        // Another stack stops as ever.
        Assert.True((await rig.Stacks.StopAsync(rig.Other.Id)).Ok);
    }

    [Fact]
    public async Task Over_a_Docker_API_its_own_stack_is_not_deployed_at_all_rather_than_left_down()
    {
        using var rig = new Rig(ConnectionType.Tcp);

        var (ok, output) = await rig.Stacks.DeployAsync(rig.Own.Id);

        Assert.False(ok);
        Assert.Equal(SelfHandover.NoHandover, output);
        Assert.DoesNotContain(rig.Hosts.Commands, c => c.Contains("docker-compose.yml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_of_its_own_stack_does_not_wait_for_a_health_gate_this_process_will_not_see()
    {
        using var rig = new Rig();

        var outcome = await rig.Updates.RunUpdateAsync(rig.Own.Id, preBackup: false, new UpdateSafety(TimeSpan.FromMinutes(2), Rollback: true));

        Assert.True(outcome.Ok);
        Assert.Contains(SelfHandover.Marker, outcome.Message);
        Assert.Contains(rig.Hosts.Commands, c => c.Contains("--name matdock-self-update", StringComparison.Ordinal) && c.Contains(" pull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Its_own_container_is_not_stopped_paused_killed_removed_or_recreated_and_a_restart_goes_through_the_helper()
    {
        using var rig = new Rig();

        foreach (var action in new[] { ContainerAction.Stop, ContainerAction.Kill, ContainerAction.Pause })
        {
            Assert.Equal((false, ContainerService.SelfRefusal), await rig.Containers.ActionAsync(rig.Env, SelfId[..12], action));
        }

        Assert.Equal((false, ContainerService.SelfRefusal), await rig.Containers.RemoveAsync(rig.Env, SelfId[..12], force: true, volumes: false));
        Assert.Equal((false, ContainerService.SelfRefusal), await rig.Containers.RecreateAsync(rig.Env, SelfId, pull: true));
        Assert.DoesNotContain(rig.Hosts.Commands, c => c.Contains(" stop ", StringComparison.Ordinal) || c.Contains(" rm ", StringComparison.Ordinal));

        var (restarted, said) = await rig.Containers.ActionAsync(rig.Env, SelfId[..12], ContainerAction.Restart);
        Assert.True(restarted);
        Assert.Contains(SelfHandover.Marker, said);
        Assert.Contains(rig.Hosts.Commands, c => c.Contains("-c \"sleep 5; docker restart " + SelfId + "\"", StringComparison.Ordinal));

        // A name is looked up first.
        rig.Hosts.Respond = c => c.Contains("{{.Id}}", StringComparison.Ordinal) ? SelfId : null;
        Assert.Equal((false, ContainerService.SelfRefusal), await rig.Containers.ActionAsync(rig.Env, "matdock-web-1", ContainerAction.Stop));
        Assert.True((await rig.Containers.ActionAsync(rig.Env, "0123456789ab", ContainerAction.Stop)).Success);
    }

    [Fact]
    public void A_backup_that_stops_the_containers_of_a_volume_leaves_MatDock_running_and_says_so()
    {
        var hosts = new FakeHostSessionFactory();
        hosts.Containers.Add(JsonSerializer.Serialize(new { ID = SelfId[..12], Names = "matdock-web-1", State = "running" }));
        hosts.Containers.Add(JsonSerializer.Serialize(new { ID = "0123456789ab", Names = "viewer", State = "running" }));
        hosts.Respond = c => c.Contains("ps", StringComparison.Ordinal) && c.Contains("volume=", StringComparison.Ordinal) ? string.Join("\n", hosts.Containers) : null;
        using var session = hosts.Create(new SshConnectionSettings { Host = "nas", Username = "me", ConnectionType = ConnectionType.Local });

        var result = ContainerQuiesce.StopRunning(session, "docker", "matdock_data", 30, SelfId);

        Assert.Equal(new[] { "0123456789ab" }, result.StoppedIds);
        Assert.True(result.SelfKept);
        Assert.Contains("MatDock itself uses this volume", result.Warning);
        Assert.DoesNotContain(hosts.Commands, c => c.Contains("stop " + SelfId[..12], StringComparison.Ordinal) || c.Contains("stop '" + SelfId[..12], StringComparison.Ordinal));
    }
}
