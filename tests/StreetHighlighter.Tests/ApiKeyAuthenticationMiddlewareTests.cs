using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using StreetHighlighter.Infrastructure;

namespace StreetHighlighter.Tests;

public class ApiKeyAuthenticationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenApiKeyIsNotConfigured_AllowsMapRequest()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(apiKey: null, () => nextCalled = true);
        var context = CreateMapRequest();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenApiKeyIsConfigured_RejectsMissingOrInvalidKey()
    {
        foreach (var suppliedKey in new[] { (string?)null, "wrong-key" })
        {
            var nextCalled = false;
            var middleware = CreateMiddleware("secret-key", () => nextCalled = true);
            var context = CreateMapRequest();
            if (suppliedKey is not null)
            {
                context.Request.Headers["X-API-Key"] = suppliedKey;
            }

            await middleware.InvokeAsync(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.Equal("ApiKey", context.Response.Headers.WWWAuthenticate.ToString());
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenApiKeyMatches_AllowsMapRequest()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware("secret-key", () => nextCalled = true);
        var context = CreateMapRequest();
        context.Request.Headers["X-API-Key"] = "secret-key";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenApiKeyIsConfigured_ProtectsStreetListRequest()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware("secret-key", () => nextCalled = true);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/streets";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenApiKeyIsConfigured_AllowsHealthCheck()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware("secret-key", () => nextCalled = true);
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    private static ApiKeyAuthenticationMiddleware CreateMiddleware(string? apiKey, Action onNext)
    {
        var values = apiKey is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["API_KEY"] = apiKey };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ApiKeyAuthenticationMiddleware(_ =>
        {
            onNext();
            return Task.CompletedTask;
        }, configuration);
    }

    private static DefaultHttpContext CreateMapRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/map";
        return context;
    }
}
