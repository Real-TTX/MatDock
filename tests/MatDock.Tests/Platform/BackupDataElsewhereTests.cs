using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using MatDock.Core.Volumes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// A volume that only points at a NAS share or a host folder (Plex's movies) is left out of stack and scheduled backups; a volume of its own
/// is backed up, and one asked for by hand too.
/// </summary>
public sealed class BackupDataElsewhereTests : IDisposable
{
    // What `docker volume inspect --format '{{json .}}'` says for each kind.
    private static readonly Dictionary<string, string> Volumes = new()
    {
        ["plex_movies"] = """{"Name":"plex_movies","Driver":"local","Mountpoint":"/var/lib/docker/volumes/plex_movies/_data","Options":{"type":"nfs","o":"addr=192.168.1.10,nfsvers=4","device":":/volume1/Movies"},"Labels":{}}""",
        ["plex_music"] = """{"Name":"plex_music","Driver":"local","Options":{"type":"cifs","o":"username=plex","device":"//nas/Music"},"Labels":{}}""",
        ["plex_photos"] = """{"Name":"plex_photos","Driver":"local","Options":{"type":"none","o":"bind","device":"/mnt/nas/photos"},"Labels":{}}""",
        ["plex_cloud"] = """{"Name":"plex_cloud","Driver":"rclone","Options":{},"Labels":{}}""",
        ["plex_config"] = """{"Name":"plex_config","Driver":"local","Mountpoint":"/var/lib/docker/volumes/plex_config/_data","Options":null,"Labels":{"com.docker.compose.project":"plex"}}""",
    };

    private readonly MatDockFactory _app = new();
    private readonly long _envId;

    public BackupDataElsewhereTests()
    {
        _envId = _app.AddLocalEnvironment("nas-host");
        _app.Hosts.Respond = command =>
            command.Contains(" volume inspect ", StringComparison.Ordinal) && command.Contains("--format", StringComparison.Ordinal)
                ? Volumes.FirstOrDefault(v => command.Contains("'" + v.Key + "'", StringComparison.Ordinal)).Value
                : null;
    }

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData("plex_movies", true, ":/volume1/Movies")]
    [InlineData("plex_music", true, "//nas/Music")]
    [InlineData("plex_photos", true, "/mnt/nas/photos")]
    [InlineData("plex_cloud", true, "driver rclone")]
    [InlineData("plex_config", false, null)]
    public void Tells_a_volume_whose_data_lives_elsewhere(string volume, bool elsewhere, string? where)
    {
        var detail = DockerVolumeDetail.Parse(Volumes[volume])!;
        Assert.Equal(elsewhere, detail.HoldsDataElsewhere);
        if (where is not null)
        {
            Assert.Equal(where, detail.Elsewhere);
        }
    }

    [Fact]
    public async Task A_stack_or_schedule_backup_leaves_the_share_out_and_keeps_the_own_data()
    {
        using var scope = _app.Services.CreateScope();
        var backups = scope.ServiceProvider.GetRequiredService<VolumeBackupService>();
        var env = (await scope.ServiceProvider.GetRequiredService<EnvironmentService>().GetAsync(_envId))!;

        var movies = await backups.BackupAsync(env, "plex_movies", target: null, skipDataElsewhere: true);
        Assert.True(movies.Skipped);
        Assert.False(movies.Success);
        Assert.Contains(":/volume1/Movies", movies.Message);
        Assert.DoesNotContain(_app.Hosts.Commands, c => c.Contains("plex_movies:", StringComparison.Ordinal) && c.Contains(" run ", StringComparison.Ordinal));

        var config = await backups.BackupAsync(env, "plex_config", target: null, skipDataElsewhere: true);
        Assert.False(config.Skipped);
        Assert.True(config.Success, config.Message);

        // Asked for by hand, the share is backed up as before.
        var byHand = await backups.BackupAsync(env, "plex_photos", target: null);
        Assert.False(byHand.Skipped);
        Assert.True(byHand.Success, byHand.Message);
    }

    [Fact]
    public async Task A_scheduled_backup_says_what_it_left_out()
    {
        using var scope = _app.Services.CreateScope();
        var action = scope.ServiceProvider.GetServices<IScheduleAction>().Single(a => a.Type == ScheduleAction.Backup);
        var task = new ScheduledTask
        {
            Name = "nightly", Action = ScheduleAction.Backup, EnvironmentId = _envId,
            OptionsJson = new ScheduleOptions { VolumesCsv = "plex_config\nplex_movies\nplex_music" }.ToJson(),
        };

        var (ok, summary) = await action.ExecuteAsync(task);

        Assert.True(ok, summary);
        Assert.StartsWith("1/1 volumes backed up", summary);
        Assert.Contains("plex_movies, plex_music", summary);
    }
}
