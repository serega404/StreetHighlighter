using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreetHighlighter.Configuration;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class GeoDataServiceTests
{
    private class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "StreetHighlighter.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private class ThrowingExternalHttpService : IExternalHttpService
    {
        public Task<HttpResponseMessage> SendAsync(
            string clientName,
            ExternalServiceOptions options,
            Func<HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("External HTTP should not have been called on cache hit.");
        }
    }

    private sealed class RespondingExternalHttpService(Func<string, HttpResponseMessage> responseFactory)
        : IExternalHttpService
    {
        public int CallCount { get; private set; }

        public Task<HttpResponseMessage> SendAsync(
            string clientName,
            ExternalServiceOptions options,
            Func<HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(clientName));
        }
    }

    [Fact]
    public async Task GetCityBoundsAsync_NegativeCacheHit_ReturnsNullWithoutCallingHttp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sh_geo_test_{Guid.NewGuid():N}");
        try
        {
            var geoCacheDir = Path.Combine(tempDir, "Cache", "Geo");
            Directory.CreateDirectory(geoCacheDir);

            // Pre-create negative cache file for "nonexistentcity"
            var cityName = "NonExistentCity";
            using var md5 = MD5.Create();
            var hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(cityName.ToLowerInvariant())));
            var notFoundFile = Path.Combine(geoCacheDir, $"bounds_notfound_{hash}.json");
            await File.WriteAllTextAsync(notFoundFile, DateTime.UtcNow.ToString("O"));

            var env = new FakeWebHostEnvironment { ContentRootPath = tempDir };
            var options = Options.Create(new ExternalServicesOptions
            {
                Nominatim = new ExternalServiceOptions { Url = "https://example.com/nominatim" }
            });

            var service = new GeoDataService(
                new ThrowingExternalHttpService(),
                options,
                env,
                NullLogger<GeoDataService>.Instance);

            var result = await service.GetCityBoundsAsync(cityName);

            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetCityBoundsAsync_ExpiredPositiveCache_RefreshesFromNominatim()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sh_geo_test_{Guid.NewGuid():N}");
        try
        {
            var cityName = $"TtlCity-{Guid.NewGuid():N}";
            var geoCacheDir = Path.Combine(tempDir, "Cache", "Geo");
            Directory.CreateDirectory(geoCacheDir);
            var cacheFile = Path.Combine(geoCacheDir, GetBoundsCacheFileName(cityName));
            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new GeoBounds(1, 2, 3, 4)));
            File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddDays(-8));

            var http = new RespondingExternalHttpService(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"class\":\"place\",\"addresstype\":\"city\",\"boundingbox\":[\"10\",\"11\",\"20\",\"21\"]}]")
            });
            var service = CreateService(tempDir, http);

            var result = await service.GetCityBoundsAsync(cityName);

            Assert.Equal(new GeoBounds(10, 20, 11, 21), result);
            Assert.Equal(1, http.CallCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetStreetGeometryAsync_ExpiredCache_RefreshesFromOverpass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sh_geo_test_{Guid.NewGuid():N}");
        try
        {
            var cityName = $"TtlCity-{Guid.NewGuid():N}";
            var bounds = new GeoBounds(1, 3, 2, 4);
            var streets = new List<string> { "Main Street" };
            var geoCacheDir = Path.Combine(tempDir, "Cache", "Geo");
            Directory.CreateDirectory(geoCacheDir);
            var keyInput = $"{cityName.ToLowerInvariant()}_1.000,3.000,2.000,4.000_main street";
            var cacheFile = Path.Combine(geoCacheDir, $"streets_{CreateMd5(keyInput)}.json");
            await File.WriteAllTextAsync(
                cacheFile,
                JsonSerializer.Serialize(new List<GeoPath> { new([new GeoPoint(0, 0)]) }));
            File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddDays(-2));

            var http = new RespondingExternalHttpService(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"elements\":[{\"geometry\":[{\"lat\":10,\"lon\":20},{\"lat\":11,\"lon\":21}]}]}")
            });
            var service = CreateService(tempDir, http);

            var result = await service.GetStreetGeometryAsync(cityName, streets, bounds);

            Assert.Single(result);
            Assert.Equal([new GeoPoint(10, 20), new GeoPoint(11, 21)], result[0].Points);
            Assert.Equal(1, http.CallCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static GeoDataService CreateService(string contentRoot, IExternalHttpService http)
    {
        var options = Options.Create(new ExternalServicesOptions
        {
            Nominatim = new ExternalServiceOptions { Url = "https://example.test/nominatim" },
            Overpass = new ExternalServiceOptions { Url = "https://example.test/overpass" }
        });

        return new GeoDataService(
            http,
            options,
            new FakeWebHostEnvironment { ContentRootPath = contentRoot },
            NullLogger<GeoDataService>.Instance);
    }

    private static string GetBoundsCacheFileName(string cityName)
    {
        var normalized = cityName.Trim().ToLowerInvariant();
        var safeChars = normalized.Where(char.IsAsciiLetterOrDigit).Take(24).ToArray();
        var prefix = safeChars.Length > 0 ? new string(safeChars) + "_" : string.Empty;
        return $"bounds_{prefix}{CreateMd5(normalized)}.json";
    }

    private static string CreateMd5(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input)));
}
