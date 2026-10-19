using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Volumes;
using MatDock.Web.Infrastructure;
using Xunit;

namespace MatDock.Tests;

public class ContainerAlertsTests
{
    private const long T = 1_760_000_000;

    private static string Ev(string action, string id, string name, long seconds, string? exitCode = null, string project = "shop")
    {
        var exit = exitCode is null ? "" : $",\"exitCode\":\"{exitCode}\"";
        return $"{{\"Type\":\"container\",\"Action\":\"{action}\",\"Actor\":{{\"ID\":\"{id}\",\"Attributes\":{{\"name\":\"{name}\",\"com.docker.compose.project\":\"{project}\"{exit}}}}},\"time\":{seconds},\"timeNano\":{seconds}000000000}}";
    }

    [Fact]
    public void A_die_with_an_error_code_is_an_alert()
    {
        var alerts = ContainerAlerts.Detect(Ev("die", "aaa", "shop-db-1", T, "1"), "local");
        var alert = Assert.Single(alerts);
        Assert.Equal(ScheduleEvent.ContainerDied, alert.Event);
        Assert.Equal("shop-db-1", alert.ContainerName);
        Assert.Equal("shop", alert.Project);
        Assert.Equal("Container shop-db-1 exited with code 1 on local.", alert.Detail);
    }

    [Fact]
    public void A_stop_is_no_alert_and_a_clean_exit_neither()
    {
        var output = string.Join("\n",
            Ev("kill", "aaa", "web", T), Ev("die", "aaa", "web", T + 2, "143"), Ev("stop", "aaa", "web", T + 2),
            Ev("die", "bbb", "migrate", T + 5, "0"));
        Assert.Empty(ContainerAlerts.Detect(output, "local"));
    }

    [Fact]
    public void A_die_long_after_a_kill_is_an_alert_again()
        => Assert.Single(ContainerAlerts.Detect(Ev("kill", "aaa", "web", T) + "\n" + Ev("die", "aaa", "web", T + 300, "1"), "local"));

    [Fact]
    public void Out_of_memory_is_an_alert_even_with_a_kill()
    {
        var output = string.Join("\n", Ev("oom", "aaa", "java", T), Ev("kill", "aaa", "java", T), Ev("die", "aaa", "java", T + 1, "137"));
        var alert = Assert.Single(ContainerAlerts.Detect(output, "prod"));
        Assert.Contains("lack of memory", alert.Detail);
    }

    [Fact]
    public void A_crash_loop_is_one_alert_per_window_and_unhealthy_is_its_own()
    {
        var output = string.Join("\n",
            Ev("die", "aaa", "api", T, "2"), Ev("start", "aaa", "api", T + 1), Ev("die", "aaa", "api", T + 5, "2"),
            Ev("health_status: healthy", "ccc", "web", T + 6), Ev("health_status: unhealthy", "ccc", "web", T + 7),
            Ev("health_status: unhealthy", "ccc", "web", T + 37), "not json", "");
        var alerts = ContainerAlerts.Detect(output, "local");
        Assert.Equal(2, alerts.Count);
        Assert.Equal(ScheduleEvent.ContainerDied, alerts[0].Event);
        Assert.Equal(ScheduleEvent.ContainerUnhealthy, alerts[1].Event);
        Assert.Equal("Container web is unhealthy on local.", alerts[1].Detail);
    }

    [Fact]
    public void The_events_command_reads_a_closed_window()
        => Assert.Equal(
            VolumeCommands.PathPrefix + "docker events --since 100 --until 160 --filter type=container --filter event=start --filter event=kill --filter event=oom --filter event=die --filter event=health_status --format '{{json .}}'",
            ContainerAlerts.EventsCommand("docker", 100, 160));

    [Fact]
    public void The_monitor_continues_where_it_stopped_but_not_further_back_than_ten_minutes()
    {
        Assert.Equal((1000L, 1000L), ContainerAlertMonitor.Window(null, 1000));
        Assert.Equal((940L, 1000L), ContainerAlertMonitor.Window(940, 1000));
        Assert.Equal((400L, 1000L), ContainerAlertMonitor.Window(10, 1000));
    }
}
