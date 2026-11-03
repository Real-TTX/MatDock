using MatDock.Core.Configuration;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Tests.Platform;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>What MatDock costs a host, and what keeps two MatDocks on one host apart.</summary>
public class HostLoadTests
{
    [Fact]
    public async Task Docker_stats_of_a_host_is_asked_once_for_every_page_and_job_within_its_window()
    {
        using var db = new TestDatabase();
        var options = Options.Create(new MatDockOptions());
        var environments = new EnvironmentService(db.Context, new FakeSecretProtector(), new FakeConnectionService(), options);
        var hosts = new FakeHostSessionFactory();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var containers = new ContainerService(hosts, environments, options, NullLogger<ContainerService>.Instance, cache: cache);
        var env = new DockerEnvironment { Name = "server2", Host = "server2", Username = "me", IsEnabled = true };
        db.Context.Environments.Add(env);
        db.Context.SaveChanges();

        for (var i = 0; i < 5; i++)
        {
            await containers.ListAsync(env); // the containers page, the dashboard, the metrics …
        }

        Assert.Equal(5, hosts.Commands.Count(c => c.Contains(" ps -a", StringComparison.Ordinal))); // the list itself stays fresh
        Assert.Single(hosts.Commands, c => c.Contains(" stats --no-stream", StringComparison.Ordinal));

        await containers.ListAsync(env, includeStats: false);
        Assert.Single(hosts.Commands, c => c.Contains(" stats --no-stream", StringComparison.Ordinal));
    }

    [Fact]
    public void An_installation_keeps_its_id_for_the_names_of_its_cookies()
    {
        var dir = Directory.CreateTempSubdirectory("matdock-instance").FullName;
        try
        {
            var paths = new AppPaths(dir);
            var id = paths.InstanceId();
            Assert.Matches("^[0-9a-f]{8}$", id);
            Assert.Equal(id, new AppPaths(dir).InstanceId());
            Assert.NotEqual(id, new AppPaths(Directory.CreateTempSubdirectory("matdock-instance2").FullName).InstanceId());

            File.WriteAllText(Path.Combine(dir, "config", "instance-id"), "not an id");
            Assert.Matches("^[0-9a-f]{8}$", paths.InstanceId());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
