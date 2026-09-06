using System.Net;
using MatDock.Core.Proxies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class MatcadProxyTests
{
    private static readonly ProxyTarget Target = new("http://matcad:4433/", ServerName: null, Token: "secret-key");

    [Fact]
    public async Task List_calls_api_v1_manual_with_api_key_and_maps()
    {
        var handler = new StubHandler(("""
            [ { "id": 7, "name": "app", "host": "app.example.com", "upstream": "10.0.0.5:8080", "enabled": true },
              { "id": 8, "host": "", "upstream": "x" } ]
            """, HttpStatusCode.OK));
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var routes = await provider.ListRoutesAsync(Target);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("http://matcad:4433/api/v1/routes/manual", req.Url);
        Assert.Equal("secret-key", req.ApiKey);
        // The blank-host entry is dropped; the valid one is mapped with TLS always on for Matcad.
        var route = Assert.Single(routes);
        Assert.Equal("7", route.Id);
        Assert.Equal("app.example.com", route.Host);
        Assert.Equal("10.0.0.5:8080", route.Upstream);
        Assert.True(route.Tls);
    }

    [Fact]
    public async Task Add_posts_to_api_v1_routes_with_host_and_upstream()
    {
        var handler = new StubHandler(("""{ "applied": { "ok": true } }""", HttpStatusCode.OK));
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var result = await provider.AddRouteAsync(Target, new ProxyRouteInput("APP.example.com ", " 10.0.0.5:8080", Tls: true, Path: null));

        Assert.True(result.Ok);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://matcad:4433/api/v1/routes", req.Url);
        Assert.Equal("secret-key", req.ApiKey);
        Assert.Contains("\"host\":\"APP.example.com\"", req.Body);
        Assert.Contains("\"upstream\":\"10.0.0.5:8080\"", req.Body);
        Assert.Contains("\"enabled\":true", req.Body);
    }

    [Fact]
    public async Task Add_reports_failure_when_caddy_did_not_apply()
    {
        var handler = new StubHandler(("""{ "applied": { "ok": false, "error": "acme failed" } }""", HttpStatusCode.OK));
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var result = await provider.AddRouteAsync(Target, new ProxyRouteInput("app.example.com", "10.0.0.5:8080", Tls: true, Path: null));

        Assert.False(result.Ok);
        Assert.Contains("acme failed", result.Message);
    }

    [Fact]
    public async Task Add_maps_401_to_a_clear_message()
    {
        var handler = new StubHandler(("unauthorized", HttpStatusCode.Unauthorized));
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var result = await provider.AddRouteAsync(Target, new ProxyRouteInput("app.example.com", "10.0.0.5:8080", Tls: true, Path: null));

        Assert.False(result.Ok);
        Assert.Contains("401", result.Message);
        Assert.Contains("X-Api-Key", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public async Task Remove_rejects_non_numeric_id_without_calling_the_api(string id)
    {
        var handler = new StubHandler();
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var result = await provider.RemoveRouteAsync(Target, id);

        Assert.False(result.Ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remove_deletes_api_v1_route_by_id()
    {
        var handler = new StubHandler(("""{ "ok": true }""", HttpStatusCode.OK));
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var result = await provider.RemoveRouteAsync(Target, "42");

        Assert.True(result.Ok);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("http://matcad:4433/api/v1/routes/42", req.Url);
    }

    [Fact]
    public async Task Test_hits_status_endpoint()
    {
        var handler = new StubHandler(("""{ "ok": true, "baseDomain": "example.com" }""", HttpStatusCode.OK));
        var provider = new MatcadProxyProvider(new StubFactory(handler), NullLogger<MatcadProxyProvider>.Instance);

        var result = await provider.TestAsync(Target);

        Assert.True(result.Ok);
        Assert.Contains("example.com", result.Message);
        Assert.Equal("http://matcad:4433/api/v1/status", Assert.Single(handler.Requests).Url);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? ApiKey, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> _responses;
        public List<CapturedRequest> Requests { get; } = new();

        public StubHandler(params (string Body, HttpStatusCode Status)[] responses)
            => _responses = new Queue<(string, HttpStatusCode)>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("X-Api-Key", out var keys);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(),
                keys is null ? null : string.Join(",", keys), body));

            var (respBody, status) = _responses.Count > 0 ? _responses.Dequeue() : ("{}", HttpStatusCode.OK);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
