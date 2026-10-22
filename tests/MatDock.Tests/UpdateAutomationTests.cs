using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Registries;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Tests.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

internal sealed class RecordingNotifications : INotificationService
{
    public List<(string Subject, string Body)> Sent { get; } = [];

    public Task NotifyBackupResultAsync(string title, string summary, bool success, CancellationToken ct = default) => Task.CompletedTask;

    public Task<(bool Ok, string Message)> SendTestAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));

    public Task<(bool Ok, string Message)> NotifyAsync(string subject, string body, CancellationToken ct = default)
    {
        Sent.Add((subject, body));
        return Task.FromResult((true, "sent"));
    }
}

/// <summary>The real services over a test database, Docker as <see cref="FakeHostSessionFactory"/> and a registry that answers from a list.</summary>
internal sealed class UpdateRig : IDisposable
{
    public const string Compose = "services:\n  web:\n    image: localhost:5000/app:1.2.0\n";

    public TestDatabase Db { get; } = new();
    public FakeHostSessionFactory Hosts { get; } = new();
    public FakeConnectionService Connection { get; } = new();
    public RecordingNotifications Notes { get; } = new();
    public UpdateAutomationState State { get; } = new();

    /// <summary>What the registry lists for <c>localhost:5000/app</c>, and the digest it answers for any tag (null = none).</summary>
    public List<string> Tags { get; } = ["1.2.0", "1.3.0", "2.0.0", "latest"];

    public string? RegistryDigest { get; set; }
    public int RegistryCalls { get; private set; }

    public StackService Stacks { get; }
    public UpdateService Updates { get; }
    public UpdatePolicyService Policies { get; }
    public UpdateAutomation Automation { get; }
    public DockerEnvironment Env { get; }
    public Stack Stack { get; }

    public UpdateRig(string compose = Compose, string runningImage = "localhost:5000/app:1.2.0")
    {
        var options = Options.Create(new MatDockOptions());
        var secrets = new FakeSecretProtector();
        var environments = new EnvironmentService(Db.Context, secrets, Connection, options);
        var registries = new RegistryService(Db.Context, secrets,
            new RegistryApiClient(new Factory(new Registry(this)), NullLogger<RegistryApiClient>.Instance));
        Stacks = new StackService(Db.Context, Hosts, environments, new MatDock.Core.Git.GitCredentialService(Db.Context, secrets),
            new MatDock.Core.Git.GitRepositoryService(NullLogger<MatDock.Core.Git.GitRepositoryService>.Instance), registries,
            new NoopScheduleEventBus(), options, NullLogger<StackService>.Instance);
        var containers = new ContainerService(Hosts, environments, options, NullLogger<ContainerService>.Instance);
        Updates = new UpdateService(containers, Connection, environments, registries, Stacks, null!, new UpdateCheckCache(), Db.Context, NullLogger<UpdateService>.Instance);
        Policies = new UpdatePolicyService(Db.Context);
        Automation = new UpdateAutomation(environments, Stacks, Updates, Policies, Notes, State, Db.Context, NullLogger<UpdateAutomation>.Instance);

        Env = new DockerEnvironment { Name = "rig", ConnectionType = ConnectionType.Local, Status = EnvironmentStatus.Online, IsEnabled = true };
        Db.Context.Environments.Add(Env);
        Db.Context.SaveChanges();
        Stack = new Stack { Name = "shop", EnvironmentId = Env.Id, ComposeYaml = compose };
        Db.Context.Stacks.Add(Stack);
        Db.Context.SaveChanges();

        SetContainer(runningImage);
    }

    public void SetContainer(string image, string id = "c0000000001", string status = "Up 3 hours")
    {
        Hosts.Containers.Clear();
        Hosts.Containers.Add(JsonSerializer.Serialize(new
        {
            ID = id, Names = "shop-web-1", Image = image, State = "running", Status = status,
            Labels = "com.docker.compose.project=shop,com.docker.compose.service=web", Ports = string.Empty,
        }));
    }

    public Task<UpdateAutomationReport> RunAsync(DateTime utcNow, int localHour = 12)
        => Automation.RunAsync(utcNow, new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, localHour, 0, 0));

    public Task SetRuleAsync(Action<UpdatePolicy> set)
    {
        var rule = new UpdatePolicy { Scope = UpdatePolicyScope.Global, ScopeId = 0 };
        set(rule);
        return Policies.SaveAsync(rule);
    }

    public int Count(string fragment) => Hosts.Commands.Count(c => c.Contains(fragment, StringComparison.Ordinal));

    public Stack Reload() => Db.Context.Stacks.AsNoTracking().Single(s => s.Id == Stack.Id);

    public void Dispose() => Db.Dispose();

    private sealed class Registry(UpdateRig rig) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            rig.RegistryCalls++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/tags/list", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { name = "app", tags = rig.Tags }) });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (path.Contains("/manifests/", StringComparison.Ordinal) && rig.RegistryDigest is { } digest)
            {
                response.Headers.Add("Docker-Content-Digest", digest);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>The update rules at work: what is announced once, what is applied within the window, what a failure leaves behind.</summary>
public class UpdateAutomationTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Without_a_rule_nothing_is_looked_at_and_nothing_happens()
    {
        using var rig = new UpdateRig();

        var report = await rig.RunAsync(T0);

        Assert.Equal(0, report.Looked);
        Assert.Equal(0, rig.RegistryCalls);
        Assert.Empty(rig.Notes.Sent);
    }

    [Fact]
    public async Task A_new_version_is_announced_once_and_nothing_is_changed()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Minor; });

        await rig.RunAsync(T0);

        var note = Assert.Single(rig.Notes.Sent);
        Assert.Contains("Update available: shop", note.Subject);
        Assert.Contains("1.3.0", note.Body);          // the newest of the same major
        Assert.DoesNotContain("2.0.0", note.Body);
        Assert.Equal(0, rig.Count("up -d"));           // nothing was applied
        Assert.Null(rig.Reload().ImageOverridesJson);

        // The same version is not announced again, even when it is looked at again ...
        await rig.RunAsync(T0.AddHours(7));
        Assert.Single(rig.Notes.Sent);

        // ... a newer one is.
        rig.Tags.Add("1.4.0");
        await rig.RunAsync(T0.AddHours(14));
        Assert.Equal(2, rig.Notes.Sent.Count);
        Assert.Contains("1.4.0", rig.Notes.Sent[1].Body);
    }

    [Fact]
    public async Task A_stack_is_looked_at_only_as_often_as_its_rule_says()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Minor; p.CheckEveryHours = 12; });

        await rig.RunAsync(T0);
        var calls = rig.RegistryCalls;
        Assert.True(calls > 0);

        await rig.RunAsync(T0.AddHours(11));
        Assert.Equal(calls, rig.RegistryCalls); // too soon

        await rig.RunAsync(T0.AddHours(12));
        Assert.True(rig.RegistryCalls > calls);
    }

    [Fact]
    public async Task Auto_applies_the_newest_version_by_an_override_and_says_so()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; });

        var report = await rig.RunAsync(T0);

        Assert.Equal(1, report.Applied);
        var overrides = ImageOverrides.Of(rig.Reload());
        Assert.Equal("localhost:5000/app:2.0.0", overrides["web"].Image);
        Assert.Equal("localhost:5000/app:1.2.0", overrides["web"].Base); // the compose file is as it was
        Assert.Equal(UpdateRig.Compose, rig.Reload().ComposeYaml);
        Assert.True(rig.Count("up -d") >= 1);

        var note = Assert.Single(rig.Notes.Sent);
        Assert.Contains("Updated: shop", note.Subject);
        Assert.Equal("auto", rig.Db.Context.StackUpdateRuns.Single().Trigger);
    }

    [Fact]
    public async Task Outside_the_window_it_is_announced_and_inside_it_is_applied_without_a_second_announcement()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p =>
        {
            p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Minor; p.PreBackup = false; p.Rollback = false;
            p.WindowStartHour = 2; p.WindowEndHour = 4;
        });

        await rig.RunAsync(T0, localHour: 10);
        Assert.Equal(0, rig.Count("up -d"));
        Assert.Contains("Update available", Assert.Single(rig.Notes.Sent).Subject);

        await rig.RunAsync(T0.AddHours(7), localHour: 3);
        Assert.True(rig.Count("up -d") >= 1);
        Assert.Equal(2, rig.Notes.Sent.Count);
        Assert.Contains("Updated: shop", rig.Notes.Sent[1].Subject); // not announced a second time
        Assert.Equal("localhost:5000/app:1.3.0", ImageOverrides.Of(rig.Reload())["web"].Image);
    }

    [Fact]
    public async Task A_failed_update_takes_its_override_back_and_is_not_tried_again()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; });
        rig.Hosts.Fails = command => command.Contains("up -d", StringComparison.Ordinal);

        var report = await rig.RunAsync(T0);

        Assert.Equal(1, report.Failed);
        Assert.Null(rig.Reload().ImageOverridesJson); // the stack is declared as it was
        Assert.Contains("Update failed: shop", Assert.Single(rig.Notes.Sent).Subject);
        var attempts = rig.Count("up -d");

        // Later, with Docker well again: the same version is not tried again, and nothing is said again.
        rig.Hosts.Fails = null;
        await rig.RunAsync(T0.AddHours(7));
        Assert.Equal(attempts, rig.Count("up -d"));
        Assert.Single(rig.Notes.Sent);

        // A newer version is a new try.
        rig.Tags.Add("2.1.0");
        await rig.RunAsync(T0.AddHours(14));
        Assert.True(rig.Count("up -d") > attempts);
        Assert.Equal("localhost:5000/app:2.1.0", ImageOverrides.Of(rig.Reload())["web"].Image);
    }

    [Fact]
    public async Task A_pinned_service_is_left_alone()
    {
        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Latest; p.PreBackup = false; p.Rollback = false; });
        await rig.Policies.SaveAsync(new UpdatePolicy
        {
            Scope = UpdatePolicyScope.Stack, ScopeId = rig.Stack.Id, ServiceStrategiesJson = "{\"web\":\"Pinned\"}",
        });

        await rig.RunAsync(T0);

        Assert.Equal(0, rig.Count("up -d"));
        Assert.Empty(rig.Notes.Sent);
    }

    [Fact]
    public async Task A_moved_tag_is_pulled_in_Auto_without_an_override()
    {
        using var rig = new UpdateRig(compose: "services:\n  web:\n    image: localhost:5000/app:latest\n", runningImage: "localhost:5000/app:latest");
        rig.Connection.Images = [new DockerImage { Id = "1", Repository = "localhost:5000/app", Tag = "latest", Digest = "sha256:" + new string('a', 64) }];
        rig.RegistryDigest = "sha256:" + new string('b', 64);
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Tag; p.PreBackup = false; p.Rollback = false; });

        var report = await rig.RunAsync(T0);

        Assert.Equal(1, report.Applied);
        Assert.Null(rig.Reload().ImageOverridesJson);
        Assert.True(rig.Count("pull") >= 1);
    }

    [Fact]
    public async Task A_tag_that_did_not_move_is_left_alone()
    {
        using var rig = new UpdateRig(compose: "services:\n  web:\n    image: localhost:5000/app:latest\n", runningImage: "localhost:5000/app:latest");
        var same = "sha256:" + new string('a', 64);
        rig.Connection.Images = [new DockerImage { Id = "1", Repository = "localhost:5000/app", Tag = "latest", Digest = same }];
        rig.RegistryDigest = same;
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Auto; p.Strategy = UpdateStrategy.Tag; });

        var report = await rig.RunAsync(T0);

        Assert.Equal(0, report.Applied);
        Assert.Empty(rig.Notes.Sent);
        Assert.Equal(0, rig.Count("up -d"));
    }

    // ---- a rollback must not bring the new image back through the override ----

    [Fact]
    public async Task A_rollback_puts_the_old_image_back_in_the_override_too()
    {
        using var rig = new UpdateRig();
        await rig.Stacks.SetImageOverrideAsync(rig.Stack.Id, "web", "localhost:5000/app:2.0.0"); // the new version is declared ...
        rig.Hosts.Respond = command =>
        {
            var updated = rig.Count("up -d") > 0;
            if (command.Contains(" ps -a", StringComparison.Ordinal))
            {
                return JsonSerializer.Serialize(new
                {
                    ID = updated ? "c0000000002" : "c0000000001", Names = "shop-web-1", Image = updated ? "localhost:5000/app:2.0.0" : "localhost:5000/app:1.2.0",
                    State = "running", Status = updated ? "Up 2 seconds (unhealthy)" : "Up 3 hours",
                    Labels = "com.docker.compose.project=shop,com.docker.compose.service=web", Ports = string.Empty,
                });
            }

            // ... while the running container still has the old one; after the update it has the new one.
            return command.Contains("inspect --format", StringComparison.Ordinal)
                ? $"web localhost:5000/app:{(updated ? "2.0.0" : "1.2.0")} sha256:{(updated ? "bbbbbbbbbbbb" : "aaaaaaaaaaaa")}\n"
                : null;
        };
        var poll = UpdateService.GatePoll;
        UpdateService.GatePoll = TimeSpan.FromMilliseconds(5);
        try
        {
            var outcome = await rig.Updates.RunUpdateAsync(rig.Stack.Id, preBackup: false, new UpdateSafety(TimeSpan.FromSeconds(2), Rollback: true));

            Assert.False(outcome.Ok);
            Assert.True(outcome.RolledBack);
            // The rollback deploys what the stack declares: that must be the old image (the compose file's own), not the new one.
            Assert.Null(rig.Reload().ImageOverridesJson);
            Assert.Equal(UpdateRunOutcome.RolledBack, rig.Db.Context.StackUpdateRuns.Single().Outcome);
        }
        finally
        {
            UpdateService.GatePoll = poll;
        }
    }
}
