using System.Net;
using System.Buffers;
using StreetHighlighter.Configuration;

namespace StreetHighlighter.Services;

public static class ExternalHttpClientNames
{
    public const string Nominatim = "Nominatim";
    public const string Overpass = "Overpass";
    public const string Tiles = "Tiles";
}

public interface IExternalHttpService
{
    Task<HttpResponseMessage> SendAsync(
        string clientName,
        ExternalServiceOptions options,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken);
}

public sealed class ExternalHttpService : IExternalHttpService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExternalHttpService> _logger;

    public ExternalHttpService(
        IHttpClientFactory httpClientFactory,
        ILogger<ExternalHttpService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HttpResponseMessage> SendAsync(
        string clientName,
        ExternalServiceOptions options,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(clientName);

        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            using var request = requestFactory();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            try
            {
                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token);

                if (!IsTransient(response.StatusCode) || attempt == options.MaxRetries)
                {
                    try
                    {
                        await BufferResponseContentAsync(clientName, response, options.MaxResponseBytes, timeoutSource.Token);
                    }
                    catch
                    {
                        response.Dispose();
                        throw;
                    }
                    return response;
                }

                var delay = GetRetryDelay(response, options, attempt);
                response.Dispose();
                LogRetry(clientName, attempt, options.MaxRetries, delay, $"HTTP {(int)response.StatusCode}");
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == options.MaxRetries)
                {
                    throw new TimeoutException(
                        $"{clientName} request timed out after {options.TimeoutSeconds} seconds.",
                        ex);
                }

                var delay = GetRetryDelay(options, attempt);
                LogRetry(clientName, attempt, options.MaxRetries, delay, "timeout");
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < options.MaxRetries)
            {
                var delay = GetRetryDelay(options, attempt);
                LogRetry(clientName, attempt, options.MaxRetries, delay, ex.GetType().Name);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("External HTTP retry loop completed unexpectedly.");
    }

    private static async Task BufferResponseContentAsync(
        string clientName,
        HttpResponseMessage response,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxResponseBytes)
        {
            throw new ExternalServiceException(
                clientName,
                $"Response body exceeds the configured limit of {maxResponseBytes} bytes.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maxResponseBytes, 64 * 1024));
        var rentedBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(rentedBuffer.AsMemory(0, rentedBuffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maxResponseBytes)
                {
                    throw new ExternalServiceException(
                        clientName,
                        $"Response body exceeds the configured limit of {maxResponseBytes} bytes.");
                }

                await output.WriteAsync(rentedBuffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        var originalContent = response.Content;
        var bufferedContent = new ByteArrayContent(output.GetBuffer(), 0, checked((int)output.Length));
        foreach (var header in originalContent.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bufferedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = bufferedContent;
        originalContent.Dispose();
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage response,
        ExternalServiceOptions options,
        int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter <= MaxRetryDelay ? retryAfter : MaxRetryDelay;
        }

        if (response.Headers.RetryAfter?.Date is { } retryDate)
        {
            var diff = retryDate - DateTimeOffset.UtcNow;
            if (diff > TimeSpan.Zero)
            {
                return diff <= MaxRetryDelay ? diff : MaxRetryDelay;
            }
        }

        return GetRetryDelay(options, attempt);
    }

    private static TimeSpan GetRetryDelay(ExternalServiceOptions options, int attempt)
    {
        var baseMilliseconds = Math.Min(
            options.RetryDelayMilliseconds * Math.Pow(2, attempt),
            MaxRetryDelay.TotalMilliseconds);
        var jitter = Random.Shared.NextDouble() * 0.2 * baseMilliseconds;

        return TimeSpan.FromMilliseconds(baseMilliseconds + jitter);
    }

    private void LogRetry(
        string clientName,
        int attempt,
        int maxRetries,
        TimeSpan delay,
        string reason)
    {
        _logger.LogWarning(
            "External request to {ClientName} failed ({Reason}). Retry {Retry}/{MaxRetries} in {DelayMs} ms",
            clientName,
            reason,
            attempt + 1,
            maxRetries,
            delay.TotalMilliseconds);
    }
}
