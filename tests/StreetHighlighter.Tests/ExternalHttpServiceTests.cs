using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StreetHighlighter.Configuration;
using StreetHighlighter.Services;

namespace StreetHighlighter.Tests;

public class ExternalHttpServiceTests
{
    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task SendAsync_ResponseExceedsDeclaredLimit_ThrowsExternalServiceException()
    {
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[2_048])
        }));
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<ExternalServiceException>(() => service.SendAsync(
            ExternalHttpClientNames.Nominatim,
            CreateOptions(maxResponseBytes: 1_024),
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
            CancellationToken.None));

        Assert.Equal(ExternalHttpClientNames.Nominatim, exception.ServiceName);
    }

    [Fact]
    public async Task SendAsync_UnknownLengthResponseExceedsLimit_ThrowsExternalServiceException()
    {
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[2_048])
        }));
        var service = CreateService(client);

        await Assert.ThrowsAsync<ExternalServiceException>(() => service.SendAsync(
            ExternalHttpClientNames.Overpass,
            CreateOptions(maxResponseBytes: 1_024),
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
            CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_ResponseWithinLimit_RemainsReadable()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(expected)
        }));
        var service = CreateService(client);

        using var response = await service.SendAsync(
            ExternalHttpClientNames.Tiles,
            CreateOptions(maxResponseBytes: 1_024),
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
            CancellationToken.None);

        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    private static ExternalHttpService CreateService(HttpClient client)
        => new(new StaticHttpClientFactory(client), NullLogger<ExternalHttpService>.Instance);

    private static ExternalServiceOptions CreateOptions(int maxResponseBytes)
        => new()
        {
            Url = "https://example.test",
            TimeoutSeconds = 5,
            MaxRetries = 0,
            MaxResponseBytes = maxResponseBytes
        };
}
