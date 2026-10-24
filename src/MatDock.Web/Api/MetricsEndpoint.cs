using MatDock.Core.Docker;
using MatDock.Core.Environments;

namespace MatDock.Web.Api;

/// <summary>
/// <c>GET /metrics</c>: the environments, hosts and containers in the Prometheus text format, from the last reading of the sampler (every five minutes;
/// <c>matdock_reading_age_seconds</c> says how old). Needs an API key, as <c>X-Api-Key</c> or as a bearer token (<c>authorization</c> of a scrape job).
/// </summary>
public static class MetricsEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext http, MetricsSnapshot snapshot, EnvironmentService environments)
    {
        if (await ApiKeys.CheckAsync(http, write: false) is { } denied)
        {
            return denied;
        }

        var all = await environments.GetEnabledAsync(http.RequestAborted);
        return Results.Text(PrometheusText.Write(all, snapshot.All.Where(r => all.Any(e => e.Id == r.Environment.Id)).ToList()), PrometheusText.ContentType);
    }
}
