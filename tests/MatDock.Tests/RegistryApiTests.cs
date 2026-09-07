using System.Net;
using System.Text;
using MatDock.Core.Registries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class RegistryApiTests
{
    private static RegistryApiClient Client(RoutingHandler handler)
        => new(new StubFactory(handler), NullLogger<RegistryApiClient>.Instance);

    [Fact]
    public async Task Catalog_follows_bearer_challenge_and_parses_repositories()
    {
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/v2/_catalog") && req.Headers.Authorization is null)
            {
                return Resp(HttpStatusCode.Unauthorized, wwwAuth: "Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\"");
            }
            if (url.StartsWith("https://ghcr.io/token"))
            {
                return Resp(HttpStatusCode.OK, json: "{\"token\":\"abc123\"}");
            }
            if (url.Contains("/v2/_catalog") && req.Headers.Authorization?.Scheme == "Bearer")
            {
                return Resp(HttpStatusCode.OK, json: "{\"repositories\":[\"org/app\",\"org/db\"]}");
            }
            return Resp(HttpStatusCode.InternalServerError);
        });

        var result = await Client(handler).ListRepositoriesAsync(new RegistryLogin("ghcr.io", "me", "pat", Insecure: false));

        Assert.Null(result.Error);
        Assert.Equal(new[] { "org/app", "org/db" }, result.Repositories);
        // The token endpoint was called with the requested scope and Basic auth (me:pat).
        var tokenReq = Assert.Single(handler.Captured, c => c.Url.StartsWith("https://ghcr.io/token"));
        Assert.Contains("scope=registry%3Acatalog%3A%2A", tokenReq.Url);
        Assert.Equal("Basic", tokenReq.AuthScheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pat")), tokenReq.AuthParam);
        // The retried catalog request carried the bearer token.
        Assert.Contains(handler.Captured, c => c.Url.Contains("/v2/_catalog") && c.AuthScheme == "Bearer" && c.AuthParam == "abc123");
    }

    [Fact]
    public async Task Test_uses_basic_auth_when_challenged_with_basic()
    {
        var handler = new RoutingHandler(req =>
            req.Headers.Authorization is null
                ? Resp(HttpStatusCode.Unauthorized, wwwAuth: "Basic realm=\"Registry\"")
                : Resp(HttpStatusCode.OK, json: "{}"));

        var result = await Client(handler).TestAsync(new RegistryLogin("registry.example.com:5000", "u", "p", Insecure: false));

        Assert.True(result.Ok);
        Assert.Contains(handler.Captured, c => c.AuthScheme == "Basic");
    }

    [Fact]
    public async Task Insecure_uses_http_scheme()
    {
        var handler = new RoutingHandler(_ => Resp(HttpStatusCode.OK, json: "{}"));

        await Client(handler).TestAsync(new RegistryLogin("registry.local:5000", "", null, Insecure: true));

        Assert.StartsWith("http://registry.local:5000/v2/", Assert.Single(handler.Captured).Url);
    }

    [Fact]
    public async Task Docker_hub_host_maps_to_registry_1()
    {
        var handler = new RoutingHandler(_ => Resp(HttpStatusCode.OK, json: "{}"));

        await Client(handler).TestAsync(new RegistryLogin("docker.io", "", null, Insecure: false));

        Assert.StartsWith("https://registry-1.docker.io/v2/", Assert.Single(handler.Captured).Url);
    }

    [Theory]
    [InlineData("library/nginx", true)]
    [InlineData("myorg/app", true)]
    [InlineData("nginx", true)]
    [InlineData("bad name", false)]
    [InlineData("/leading", false)]
    [InlineData("UP/case", false)]
    public void IsValidRepo_matches_registry_path_rules(string repo, bool expected)
        => Assert.Equal(expected, RegistryApiClient.IsValidRepo(repo));

    private static HttpResponseMessage Resp(HttpStatusCode code, string? json = null, string? wwwAuth = null)
    {
        var r = new HttpResponseMessage(code);
        if (json is not null) { r.Content = new StringContent(json, Encoding.UTF8, "application/json"); }
        if (wwwAuth is not null) { r.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuth); }
        return r;
    }

    private sealed record Captured(string Method, string Url, string? AuthScheme, string? AuthParam);

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
        public List<Captured> Captured { get; } = new();

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Captured.Add(new Captured(request.Method.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return Task.FromResult(_route(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
