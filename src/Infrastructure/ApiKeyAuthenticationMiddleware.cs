using System.Security.Cryptography;
using System.Text;

namespace StreetHighlighter.Infrastructure;

public sealed class ApiKeyAuthenticationMiddleware
{
    private const string HeaderName = "X-API-Key";
    private readonly RequestDelegate _next;
    private readonly string? _apiKey;

    public ApiKeyAuthenticationMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _apiKey = configuration["API_KEY"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isProtectedEndpoint = context.Request.Path.StartsWithSegments("/api/v1/map")
            || context.Request.Path.StartsWithSegments("/api/v1/streets");

        if (string.IsNullOrWhiteSpace(_apiKey) || !isProtectedEndpoint)
        {
            await _next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue(HeaderName, out var suppliedKey)
            && KeysMatch(_apiKey, suppliedKey.ToString()))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "ApiKey";
    }

    private static bool KeysMatch(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
