using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using StreetHighlighter.Configuration;
using StreetHighlighter.Infrastructure;
using StreetHighlighter.Models;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class MapRendererServiceTests
{
    private class FakeGeoDataService : IGeoDataService
    {
        public GeoBounds? Bounds { get; set; } = new(47.20, 38.80, 47.25, 38.90);
        public List<GeoPath> Streets { get; set; } = new();
        public bool? LastExactStreetNames { get; private set; }

        public Task<GeoBounds?> GetCityBoundsAsync(string cityName, CancellationToken cancellationToken = default)
            => Task.FromResult(Bounds);

        public Task<CityInfo?> GetCityInfoAsync(string cityName, CancellationToken cancellationToken = default)
            => Task.FromResult(Bounds == null ? null : new CityInfo(Bounds, cityName, null));

        public Task<List<string>> GetStreetNamesAsync(
            string cityName,
            GeoBounds bounds,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());

        public Task<List<GeoPath>> GetStreetGeometryAsync(
            string cityName,
            List<string> streets,
            GeoBounds? bounds = null,
            bool exactStreetNames = false,
            CancellationToken cancellationToken = default)
        {
            LastExactStreetNames = exactStreetNames;
            return Task.FromResult(Streets);
        }
    }

    private class FakeTileCacheService : ITileCacheService
    {
        private readonly byte[] _tileBytes;

        public FakeTileCacheService(byte[] tileBytes)
        {
            _tileBytes = tileBytes;
        }

        public Task<byte[]?> GetCachedTileAsync(int z, int x, int y, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(_tileBytes);

        public Task SaveTileAsync(int z, int x, int y, byte[] tileData, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<byte[]?> GetOrDownloadTileAsync(
            int z,
            int x,
            int y,
            Func<CancellationToken, Task<byte[]>> downloadFactory,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(_tileBytes);

        public void EvictTile(int z, int x, int y) { }
    }

    private class DummyExternalHttpService : IExternalHttpService
    {
        public Task<HttpResponseMessage> SendAsync(
            string clientName,
            ExternalServiceOptions options,
            Func<HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private static byte[] CreateSolidTilePng(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(256, 256));
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static MapRendererService CreateRenderer(
        byte[] tileBytes,
        IGeoDataService? geoData = null)
    {
        var geo = geoData ?? new FakeGeoDataService();
        var cache = new FakeTileCacheService(tileBytes);
        var perf = new PerformanceLogger(NullLogger<PerformanceLogger>.Instance);
        var logger = NullLogger<MapRendererService>.Instance;
        var http = new DummyExternalHttpService();
        var options = Options.Create(new ExternalServicesOptions
        {
            Tiles = new ExternalServiceOptions
            {
                Url = "https://tile.openstreetmap.org/{z}/{x}/{y}.png"
            }
        });

        return new MapRendererService(geo, cache, perf, logger, http, options);
    }

    [Fact]
    public async Task GenerateMapAsync_DefaultPreset_RendersOriginalLightTiles()
    {
        var whiteTile = CreateSolidTilePng(SKColors.White);
        var renderer = CreateRenderer(whiteTile);

        var request = new MapRequest
        {
            CityName = "Таганрог",
            Width = 256,
            Height = 256,
            Style = new StyleSettings { Preset = "default" }
        };

        var pngBytes = await renderer.GenerateMapAsync(request, "test-default");

        Assert.NotNull(pngBytes);
        Assert.True(TileCacheService.IsValidPng(pngBytes));

        using var bitmap = SKBitmap.Decode(pngBytes);
        Assert.NotNull(bitmap);

        var centerPixel = bitmap.GetPixel(128, 128);
        Assert.True(centerPixel.Red > 200 && centerPixel.Green > 200 && centerPixel.Blue > 200,
            $"Expected light pixel in default preset, got: {centerPixel}");
    }

    [Fact]
    public async Task GenerateMapAsync_DarkPreset_InvertsWhiteTileToDark()
    {
        var whiteTile = CreateSolidTilePng(SKColors.White);
        var renderer = CreateRenderer(whiteTile);

        var request = new MapRequest
        {
            CityName = "Таганрог",
            Width = 256,
            Height = 256,
            Style = new StyleSettings { Preset = "dark" }
        };

        var pngBytes = await renderer.GenerateMapAsync(request, "test-dark");

        Assert.NotNull(pngBytes);
        Assert.True(TileCacheService.IsValidPng(pngBytes));

        using var bitmap = SKBitmap.Decode(pngBytes);
        Assert.NotNull(bitmap);

        var centerPixel = bitmap.GetPixel(128, 128);
        // White tile must be inverted to near black by dark preset, NOT remain pure white!
        Assert.True(centerPixel.Red < 50 && centerPixel.Green < 50 && centerPixel.Blue < 50,
            $"Expected dark pixel in dark preset, got: {centerPixel}");
    }

    [Fact]
    public async Task GenerateMapAsync_WithStreets_RendersHighlightColor()
    {
        var whiteTile = CreateSolidTilePng(SKColors.White);
        var geoData = new FakeGeoDataService
        {
            Bounds = new GeoBounds(47.20, 38.80, 47.25, 38.90),
            Streets = new List<GeoPath>
            {
                new(new List<GeoPoint>
                {
                    new(47.22, 38.82),
                    new(47.23, 38.88)
                })
            }
        };
        var renderer = CreateRenderer(whiteTile, geoData);

        var request = new MapRequest
        {
            CityName = "Таганрог",
            HighlightStreets = new List<string> { "Петровская" },
            Width = 400,
            Height = 400,
            Style = new StyleSettings
            {
                Preset = "default",
                HighlightColor = "#FF0000",
                StrokeWidth = 10f,
                Opacity = 1.0f
            }
        };

        var pngBytes = await renderer.GenerateMapAsync(request, "test-streets");

        Assert.NotNull(pngBytes);
        using var bitmap = SKBitmap.Decode(pngBytes);
        Assert.NotNull(bitmap);

        // Find at least one distinctly red pixel drawn by the street overlay
        var foundRed = false;
        for (var x = 0; x < bitmap.Width && !foundRed; x++)
        {
            for (var y = 0; y < bitmap.Height && !foundRed; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green < 50 && pixel.Blue < 50)
                {
                    foundRed = true;
                }
            }
        }

        Assert.True(foundRed, "Expected to find red street line pixels on the rendered map.");
    }

    [Fact]
    public async Task GenerateMapAsync_ExactStreetNames_ForwardsMatchingMode()
    {
        var geoData = new FakeGeoDataService();
        var renderer = CreateRenderer(CreateSolidTilePng(SKColors.White), geoData);
        var request = new MapRequest
        {
            CityName = "Таганрог",
            HighlightStreets = ["1-й Новый переулок"],
            ExactStreetNames = true,
            Width = 256,
            Height = 256
        };

        await renderer.GenerateMapAsync(request, "test-exact-streets");

        Assert.True(geoData.LastExactStreetNames);
    }

    [Fact]
    public async Task GenerateMapAsync_WithOffsets_ShiftsRenderedContentInPixels()
    {
        var geoData = new FakeGeoDataService
        {
            Bounds = new GeoBounds(47.20, 38.80, 47.25, 38.90),
            Streets = new List<GeoPath>
            {
                new(new List<GeoPoint>
                {
                    new(47.225, 38.84),
                    new(47.225, 38.86)
                })
            }
        };
        var renderer = CreateRenderer(CreateSolidTilePng(SKColors.White), geoData);

        var baseRequest = new MapRequest
        {
            CityName = "Таганрог",
            HighlightStreets = new List<string> { "Петровская" },
            Width = 400,
            Height = 400,
            Style = new StyleSettings
            {
                HighlightColor = "#FF0000",
                StrokeWidth = 6f
            }
        };
        var shiftedRequest = new MapRequest
        {
            CityName = baseRequest.CityName,
            HighlightStreets = baseRequest.HighlightStreets,
            Width = baseRequest.Width,
            Height = baseRequest.Height,
            OffsetX = 40,
            OffsetY = -30,
            Style = baseRequest.Style
        };

        using var baseBitmap = SKBitmap.Decode(await renderer.GenerateMapAsync(baseRequest, "test-offset-base"));
        using var shiftedBitmap = SKBitmap.Decode(await renderer.GenerateMapAsync(shiftedRequest, "test-offset-shifted"));

        var baseCenter = FindRedPixelCenter(baseBitmap);
        var shiftedCenter = FindRedPixelCenter(shiftedBitmap);

        Assert.InRange(shiftedCenter.X - baseCenter.X, 39, 41);
        Assert.InRange(shiftedCenter.Y - baseCenter.Y, -31, -29);
    }

    [Fact]
    public async Task GenerateMapAsync_AddsOpenStreetMapAttributionOverlay()
    {
        var renderer = CreateRenderer(CreateSolidTilePng(SKColors.White));
        var request = new MapRequest
        {
            CityName = "Таганрог",
            Width = 400,
            Height = 200,
            Style = new StyleSettings { Preset = "default" }
        };

        var pngBytes = await renderer.GenerateMapAsync(request, "test-attribution");
        using var bitmap = SKBitmap.Decode(pngBytes);
        Assert.NotNull(bitmap);

        var darkPixels = 0;
        var lightTextPixels = 0;
        for (var x = bitmap.Width - 220; x < bitmap.Width; x++)
        {
            for (var y = bitmap.Height - 35; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 160 && pixel.Green < 160 && pixel.Blue < 160)
                {
                    darkPixels++;
                }
                if (pixel.Red > 220 && pixel.Green > 220 && pixel.Blue > 220 &&
                    x > 0 && bitmap.GetPixel(x - 1, y).Red < 160)
                {
                    lightTextPixels++;
                }
            }
        }

        Assert.True(darkPixels > 200, $"Expected a dark attribution background in the bottom-right corner; found {darkPixels} dark pixels.");
        Assert.True(lightTextPixels > 10, $"Expected light attribution text over the background; found {lightTextPixels} light pixels.");
    }

    [Fact]
    public async Task GenerateMapAsync_CityNotFound_ThrowsCityNotFoundException()
    {
        var whiteTile = CreateSolidTilePng(SKColors.White);
        var geoData = new FakeGeoDataService { Bounds = null };
        var renderer = CreateRenderer(whiteTile, geoData);

        var request = new MapRequest
        {
            CityName = "UnknownCity",
            Width = 200,
            Height = 200
        };

        await Assert.ThrowsAsync<CityNotFoundException>(() =>
            renderer.GenerateMapAsync(request, "test-not-found"));
    }

    private static (double X, double Y) FindRedPixelCenter(SKBitmap bitmap)
    {
        var xSum = 0L;
        var ySum = 0L;
        var count = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green < 50 && pixel.Blue < 50)
                {
                    xSum += x;
                    ySum += y;
                    count++;
                }
            }
        }

        Assert.True(count > 0, "Expected to find red street line pixels on the rendered map.");
        return ((double)xSum / count, (double)ySum / count);
    }
}
