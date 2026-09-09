using StreetHighlighter.Configuration;
using Xunit;

namespace StreetHighlighter.Tests;

public class ExternalServicesOptionsTests
{
    [Fact]
    public void ExternalServicesOptions_Default_IsValid()
    {
        var options = new ExternalServicesOptions
        {
            UserAgent = "StreetHighlighter/1.0 (test@example.com)",
            Nominatim = new ExternalServiceOptions
            {
                Url = "https://nominatim.openstreetmap.org/search",
                TimeoutSeconds = 10,
                MaxRetries = 2,
                RetryDelayMilliseconds = 500
            },
            Overpass = new ExternalServiceOptions
            {
                Url = "https://overpass-api.de/api/interpreter",
                TimeoutSeconds = 30,
                MaxRetries = 2,
                RetryDelayMilliseconds = 1000
            },
            Tiles = new ExternalServiceOptions
            {
                Url = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
                TimeoutSeconds = 10,
                MaxRetries = 2,
                RetryDelayMilliseconds = 250
            }
        };

        Assert.True(ExternalServicesOptions.IsValid(options));
    }

    [Theory]
    [InlineData("https://tile.openstreetmap.org/0/0/0.png")] // Missing placeholders
    [InlineData("not-a-url")]
    public void ExternalServicesOptions_InvalidTileUrl_ReturnsFalse(string tileUrl)
    {
        var options = new ExternalServicesOptions
        {
            UserAgent = "StreetHighlighter/1.0 (test@example.com)",
            Nominatim = new ExternalServiceOptions { Url = "https://nominatim.openstreetmap.org/search" },
            Overpass = new ExternalServiceOptions { Url = "https://overpass-api.de/api/interpreter" },
            Tiles = new ExternalServiceOptions { Url = tileUrl }
        };

        Assert.False(ExternalServicesOptions.IsValid(options));
    }

    [Theory]
    [InlineData(1_023)]
    [InlineData(134_217_729)]
    public void ExternalServicesOptions_InvalidResponseLimit_ReturnsFalse(int maxResponseBytes)
    {
        var options = new ExternalServicesOptions
        {
            UserAgent = "StreetHighlighter/1.0 (test@example.com)",
            Nominatim = new ExternalServiceOptions
            {
                Url = "https://nominatim.openstreetmap.org/search",
                MaxResponseBytes = maxResponseBytes
            },
            Overpass = new ExternalServiceOptions { Url = "https://overpass-api.de/api/interpreter" },
            Tiles = new ExternalServiceOptions { Url = "https://tile.openstreetmap.org/{z}/{x}/{y}.png" }
        };

        Assert.False(ExternalServicesOptions.IsValid(options));
    }
}
