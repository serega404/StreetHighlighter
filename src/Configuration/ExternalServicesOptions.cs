namespace StreetHighlighter.Configuration;

public sealed class ExternalServicesOptions
{
    public const string SectionName = "ExternalServices";

    public string UserAgent { get; set; } = "StreetHighlighter/1.0 (contact@StreetHighlighter.local)";
    public ExternalServiceOptions Nominatim { get; set; } = new();
    public ExternalServiceOptions Overpass { get; set; } = new();
    public ExternalServiceOptions Tiles { get; set; } = new();

    public static bool IsValid(ExternalServicesOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.UserAgent)
            && IsValid(options.Nominatim)
            && IsValid(options.Overpass)
            && IsValid(options.Tiles)
            && IsAbsoluteHttpUrl(options.Nominatim.Url)
            && IsAbsoluteHttpUrl(options.Overpass.Url)
            && IsValidTileUrl(options.Tiles.Url);
    }

    private static bool IsValid(ExternalServiceOptions options)
    {
        return options.TimeoutSeconds is >= 1 and <= 300
            && options.MaxRetries is >= 0 and <= 10
            && options.RetryDelayMilliseconds is >= 0 and <= 60_000
            && options.MaxResponseBytes is >= 1_024 and <= 134_217_728;
    }

    private static bool IsAbsoluteHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsValidTileUrl(string url)
    {
        return url.Contains("{z}", StringComparison.Ordinal)
            && url.Contains("{x}", StringComparison.Ordinal)
            && url.Contains("{y}", StringComparison.Ordinal)
            && IsAbsoluteHttpUrl(
                url.Replace("{z}", "0", StringComparison.Ordinal)
                    .Replace("{x}", "0", StringComparison.Ordinal)
                    .Replace("{y}", "0", StringComparison.Ordinal));
    }
}

public sealed class ExternalServiceOptions
{
    public string Url { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMilliseconds { get; set; } = 500;
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
}
