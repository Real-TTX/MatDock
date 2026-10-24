using System.Net;
using System.Net.Http.Headers;
using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Prometheus reads <c>/metrics</c> with a key (header or bearer); the pages show the stored readings as charts.</summary>
public sealed class MetricsEndpointTests : IClassFixture<MatDockFactory>
{
    private readonly MatDockFactory _site;

    public MetricsEndpointTests(MatDockFactory site) => _site = site;

    [Fact]
    public async Task Without_a_key_there_are_no_metrics()
    {
        var response = await _site.Browser().GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_scrape_with_the_key_reads_the_last_readings()
    {
        var envId = _site.AddLocalEnvironment("scraped");
        var env = _site.Db(db => db.Environments.Single(e => e.Id == envId));
        _site.Services.GetRequiredService<MetricsSnapshot>().Set(new MetricsSnapshot.Reading(env, DateTime.UtcNow,
            new HostStats { Cores = 4, Load1 = 1, MemTotalBytes = 100, MemUsedBytes = 50 },
            [new DockerContainer { Id = "a", Name = "web", Image = "nginx", State = "running", CpuPercent = 3.5, MemUsage = "10MiB / 1GiB" }]));

        var client = _site.Browser();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MatDockFactory.EngineKey);
        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("matdock_environment_up{environment=\"scraped\"} 1", text);
        Assert.Contains("matdock_host_cpu_percent{environment=\"scraped\"} 25", text);
        Assert.Contains("matdock_container_cpu_percent{environment=\"scraped\",container=\"web\",stack=\"\",image=\"nginx\"} 3.5", text);

        var header = await _site.Api(MatDockFactory.EngineKey).GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, header.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _site.Api("wrong-key").GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task The_environment_page_charts_the_stored_readings()
    {
        var envId = _site.AddLocalEnvironment("charted");
        using (var scope = _site.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<MetricsStore>();
            for (var i = 3; i > 0; i--)
            {
                await store.RecordAsync(envId, new HostStats { Cores = 2, Load1 = i / 2.0, MemTotalBytes = 100, MemUsedBytes = 30, DiskTotalBytes = 100, DiskUsedBytes = 60 }, [], DateTime.UtcNow.AddMinutes(-5 * i));
            }
        }

        var browser = _site.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);

        var html = await (await browser.GetAsync($"/Environments/Detail?id={envId}&range=6h")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"usage\"", html);
        Assert.Contains("md-spark__disk", html);
        Assert.Contains($"/Environments/Detail?id={envId}&amp;range=7d#usage", html);
        Assert.Contains("max 75 %", html);
    }
}
