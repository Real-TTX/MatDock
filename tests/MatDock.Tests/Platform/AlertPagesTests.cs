using System.Net;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The pages of the alerts: the limits and which events notify (as schedules), a channel with its secret, and the own limits of a host.</summary>
public sealed class AlertPagesTests : IClassFixture<MatDockFactory>
{
    private readonly MatDockFactory _site;

    public AlertPagesTests(MatDockFactory site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string url, params (string Key, string Value)[] fields)
    {
        var html = await (await browser.GetAsync(page)).Content.ReadAsStringAsync();
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", SignIn.AntiforgeryToken(html)) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await browser.PostAsync(url, new FormUrlEncodedContent(form));
    }

    [Fact]
    public async Task The_limits_are_saved_and_the_chosen_events_notify()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync("/Settings/Notifications")).Content.ReadAsStringAsync();
        Assert.Contains("handler=Alerts", page);
        Assert.Contains("value=\"HostThreshold\"", page);

        await PostAsync(browser, "/Settings/Notifications", "/Settings/Notifications?handler=Alerts",
            ("Alerts.Disk", "85"), ("Alerts.Mem", "90"), ("Alerts.Cpu", ""), ("Alerts.RestartCount", "4"), ("Alerts.RestartMinutes", "15"),
            ("Alerts.Notify", "HostThreshold"), ("Alerts.Notify", "ContainerRestartLoop"));

        var settings = _site.Db(db => db.NotificationSettings.Single());
        Assert.Equal((85, 90, (int?)null, 4, 15), (settings.HostDiskLimit, settings.HostMemLimit, settings.HostCpuLimit, settings.RestartLoopCount, settings.RestartLoopMinutes));
        var notify = _site.Db(db => db.ScheduledTasks.Where(s => s.Action == ScheduleAction.Notify && s.Enabled).Select(s => s.Event).ToList());
        Assert.Contains(ScheduleEvent.HostThreshold, notify);
        Assert.Contains(ScheduleEvent.ContainerRestartLoop, notify);

        // Unticked: the schedule is switched off, not deleted.
        await PostAsync(browser, "/Settings/Notifications", "/Settings/Notifications?handler=Alerts",
            ("Alerts.Disk", "85"), ("Alerts.RestartCount", "4"), ("Alerts.RestartMinutes", "15"), ("Alerts.Notify", "HostThreshold"));
        var loop = _site.Db(db => db.ScheduledTasks.Single(s => s.Event == ScheduleEvent.ContainerRestartLoop && s.Action == ScheduleAction.Notify));
        Assert.False(loop.Enabled);

        await PostAsync(browser, "/Settings/Notifications", "/Settings/Notifications?handler=Alerts", ("Alerts.Disk", "150"), ("Alerts.RestartCount", "4"), ("Alerts.RestartMinutes", "15"));
        Assert.Equal(85, _site.Db(db => db.NotificationSettings.Single()).HostDiskLimit);
    }

    [Fact]
    public async Task A_channel_is_added_with_its_secret_hidden()
    {
        var browser = await AdminAsync();
        var save = await PostAsync(browser, "/Settings/ChannelEdit", "/Settings/ChannelEdit?handler=Save",
            ("Input.Name", "Phone"), ("Input.Type", ((int)NotificationChannelType.Ntfy).ToString()), ("Input.Target", "matdock-alerts"),
            ("Input.Secret", "tk_very_secret"), ("Input.Enabled", "true"));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var channel = _site.Db(db => db.NotificationChannels.Single(c => c.Name == "Phone"));
        Assert.Equal(("matdock-alerts", NotificationChannelType.Ntfy), (channel.Target, channel.Type));
        Assert.DoesNotContain("tk_very_secret", channel.EncryptedSecret);

        var list = await (await browser.GetAsync("/Settings/Notifications")).Content.ReadAsStringAsync();
        Assert.Contains($"/Settings/ChannelEdit?id={channel.Id}", list);
        var edit = await (await browser.GetAsync($"/Settings/ChannelEdit?id={channel.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("tk_very_secret", edit);
        Assert.Contains("Stored. Empty keeps it.", edit);

        var refused = await PostAsync(browser, "/Settings/ChannelEdit", "/Settings/ChannelEdit?handler=Save",
            ("Input.Name", "Broken"), ("Input.Type", ((int)NotificationChannelType.Telegram).ToString()), ("Input.Secret", "1:A"), ("Input.Target", "not a chat"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("chat id", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_host_gets_its_own_limits()
    {
        var envId = _site.AddLocalEnvironment("limits-host");
        var browser = await AdminAsync();
        var page = $"/Environments/Detail?id={envId}";
        await PostAsync(browser, page, $"{page}&handler=Alerts", ("cpu", "0"), ("mem", ""), ("disk", "95"));

        var env = _site.Db(db => db.Environments.Single(e => e.Id == envId));
        Assert.Equal((0, (int?)null, 95), (env.AlertCpuPercent, env.AlertMemPercent, env.AlertDiskPercent));
    }
}
