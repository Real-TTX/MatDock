using System.Security.Claims;
using MatDock.Core.Configuration;
using MatDock.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

public class ApiKeyFilterTests
{
    private static int? StatusOf(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_configured_key_disables_the_api_with_503(string? configured)
    {
        Assert.Equal(503, StatusOf(ApiKeyFilter.Check(configured, "anything")));
        Assert.Equal(503, StatusOf(ApiKeyFilter.Check(configured, null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("secret-key-but-longer")]
    [InlineData("SECRET-KEY")]
    public void Missing_or_wrong_key_is_401(string? presented)
        => Assert.Equal(401, StatusOf(ApiKeyFilter.Check("secret-key", presented)));

    [Fact]
    public void Matching_key_passes_and_surrounding_whitespace_is_ignored()
    {
        Assert.Null(ApiKeyFilter.Check("secret-key", "secret-key"));
        Assert.Null(ApiKeyFilter.Check(" secret-key\n", "secret-key "));
    }

    [Fact]
    public void Errors_use_the_contract_shape()
    {
        var result = Assert.IsAssignableFrom<IValueHttpResult>(ApiKeyFilter.Check(null, null));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value, ApiResults.Json);
        Assert.StartsWith("{\"error\":", json);
    }

    [Fact]
    public void KeysMatch_compares_content_not_prefixes()
    {
        Assert.True(ApiKeyFilter.KeysMatch("abc", "abc"));
        Assert.False(ApiKeyFilter.KeysMatch("abc", "ab"));
        Assert.False(ApiKeyFilter.KeysMatch("abc", "abcd"));
        Assert.False(ApiKeyFilter.KeysMatch("abc", string.Empty));
    }

    [Fact]
    public async Task Filter_ignores_the_session_cookie_and_needs_the_key()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new MatDockOptions { Api = new ApiOptions { Key = "k1" } }))
            .BuildServiceProvider();

        // A cookie-authenticated admin without the key is still refused.
        var cookieOnly = NewContext(services, apiKey: null);
        var nextCalled = false;
        var denied = await new ApiKeyFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(cookieOnly),
            _ => { nextCalled = true; return ValueTask.FromResult<object?>(null); });
        Assert.False(nextCalled);
        Assert.Equal(401, StatusOf(denied as IResult));

        // With the key the endpoint runs, and it no longer sees the cookie's identity.
        var withKey = NewContext(services, apiKey: "k1");
        bool? authenticatedInside = null;
        await new ApiKeyFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(withKey), ctx =>
        {
            authenticatedInside = ctx.HttpContext.User.Identity?.IsAuthenticated;
            return ValueTask.FromResult<object?>(Results.Ok());
        });
        Assert.False(authenticatedInside);
    }

    [Fact]
    public async Task Filter_turns_an_exception_into_a_json_500()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new MatDockOptions { Api = new ApiOptions { Key = "k1" } }))
            .BuildServiceProvider();

        var result = await new ApiKeyFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(NewContext(services, "k1")),
            _ => throw new InvalidOperationException("boom"));

        Assert.Equal(500, StatusOf(result as IResult));
    }

    private static DefaultHttpContext NewContext(IServiceProvider services, string? apiKey)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") }, "MatDock"));
        if (apiKey is not null)
        {
            context.Request.Headers[ApiKeyFilter.HeaderName] = apiKey;
        }

        return context;
    }
}
