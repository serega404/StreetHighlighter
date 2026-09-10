using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using StreetHighlighter.Configuration;
using StreetHighlighter.Infrastructure;
using StreetHighlighter.Models;
using Microsoft.Extensions.Options;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Globalization;

namespace StreetHighlighter.Services
{
    public sealed class CityNotFoundException : Exception
    {
        public CityNotFoundException() : base("City not found")
        {
        }
    }

    public sealed class ExternalServiceException : Exception
    {
        public string ServiceName { get; }

        public ExternalServiceException(string serviceName, string message, Exception? inner = null)
            : base($"External service '{serviceName}' failed: {message}", inner)
        {
            ServiceName = serviceName;
        }
    }

    public interface IMapRendererService
    {
        Task<byte[]> GenerateMapAsync(
            MapRequest request,
            string requestId,
            CancellationToken cancellationToken = default);
    }

    public class MapRendererService : IMapRendererService
    {
        private const string AttributionFontResource =
            "StreetHighlighter.Assets.Fonts.NotoSans-Regular.ttf";
        private static readonly Lazy<SKTypeface> AttributionTypeface =
            new(LoadAttributionTypeface, LazyThreadSafetyMode.ExecutionAndPublication);
        private readonly IGeoDataService _geoData;
        private readonly ITileCacheService _tileCache;
        private readonly PerformanceLogger _perf;
        private readonly ILogger<MapRendererService> _logger;
        private readonly IExternalHttpService _externalHttp;
        private readonly ExternalServiceOptions _tileOptions;
        private readonly HttpTileSource _tileSource;

        public MapRendererService(
            IGeoDataService geoData,
            ITileCacheService tileCache,
            PerformanceLogger perf,
            ILogger<MapRendererService> logger,
            IExternalHttpService externalHttp,
            IOptions<ExternalServicesOptions> externalServices)
        {
            _geoData = geoData;
            _tileCache = tileCache;
            _perf = perf;
            _logger = logger;
            _externalHttp = externalHttp;
            _tileOptions = externalServices.Value.Tiles;
            _tileSource = new HttpTileSource(new GlobalSphericalMercator(YAxis.OSM),
                _tileOptions.Url,
                name: "Configured tile provider");
        }

        private static readonly SemaphoreSlim _renderLimiter = new(Math.Max(Environment.ProcessorCount * 2, 4));
        private static readonly SemaphoreSlim _osmTileDownloadSemaphore = new(2, 2);

        public async Task<byte[]> GenerateMapAsync(
            MapRequest request,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            if (request.Width is < 64 or > 4096 || request.Height is < 64 or > 4096)
            {
                throw new ArgumentException("Map width and height must be between 64 and 4096 pixels.");
            }

            if (request.Zoom.HasValue && request.Zoom.Value is < 0 or > 19)
            {
                throw new ArgumentException("Zoom must be between 0 and 19.");
            }

            if (request.OffsetX is < -4096 or > 4096 || request.OffsetY is < -4096 or > 4096)
            {
                throw new ArgumentException("Map offsets must be between -4096 and 4096 pixels.");
            }

            try
            {
                return await GenerateMapInternalAsync(request, cancellationToken);
            }
            finally
            {
                _perf.LogSummary(requestId);
            }
        }

        private async Task<byte[]> GenerateMapInternalAsync(
            MapRequest request,
            CancellationToken cancellationToken)
        {
            await _renderLimiter.WaitAsync(cancellationToken);
            try
            {
                GeoBounds? bounds;
                List<GeoPath> streets;

                using (_perf.Measure("Load GeoData"))
                {
                    bounds = await _geoData.GetCityBoundsAsync(request.CityName, cancellationToken);
                    if (bounds == null) throw new CityNotFoundException();

                    streets = await _geoData.GetStreetGeometryAsync(
                        request.CityName,
                        request.HighlightStreets,
                        bounds,
                        request.ExactStreetNames,
                        cancellationToken);
                }

                return await RenderMapAsync(request, bounds, streets, cancellationToken);
            }
            finally
            {
                _renderLimiter.Release();
            }
        }

        private async Task<byte[]> RenderMapAsync(
            MapRequest request,
            GeoBounds bounds,
            List<GeoPath> streets,
            CancellationToken cancellationToken)
        {
            var style = request.Style ?? new StyleSettings();

            // 2. Prepare Projection & Viewport
            var min = ToSphericalMercator(bounds.MinLat, bounds.MinLon);
            var max = ToSphericalMercator(bounds.MaxLat, bounds.MaxLon);

            var minX = Math.Min(min.x, max.x);
            var maxX = Math.Max(min.x, max.x);
            var minY = Math.Min(min.y, max.y);
            var maxY = Math.Max(min.y, max.y);

            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;

            // Ensure minimum dimensions (at least 500 meters) plus 5% padding
            var safeWidth = Math.Max(maxX - minX, 500.0) * 1.05;
            var safeHeight = Math.Max(maxY - minY, 500.0) * 1.05;

            var extent = new Extent(
                centerX - safeWidth / 2.0,
                centerY - safeHeight / 2.0,
                centerX + safeWidth / 2.0,
                centerY + safeHeight / 2.0
            );

            double resolution;

            if (request.Zoom.HasValue)
            {
                var levelInt = request.Zoom.Value;
                if (_tileSource.Schema.Resolutions.ContainsKey(levelInt))
                {
                    resolution = _tileSource.Schema.Resolutions[levelInt].UnitsPerPixel;
                }
                else
                {
                    resolution = 156543.03392804097 / Math.Pow(2, request.Zoom.Value);
                }
            }
            else
            {
                // Auto-fit
                resolution = Math.Max(extent.Width / request.Width, extent.Height / request.Height);
            }

            // Guard against division by zero
            resolution = Math.Max(resolution, 0.0001);

            // Find closest info from tile source
            var levelId = Utilities.GetNearestLevel(_tileSource.Schema.Resolutions, resolution);

            // Move the viewport opposite to the requested screen-space offset so
            // positive X/Y values move rendered map content right/down.
            var worldWidth = request.Width * resolution;
            var worldHeight = request.Height * resolution;
            var viewportCenterX = centerX - request.OffsetX * resolution;
            var viewportCenterY = centerY + request.OffsetY * resolution;
            var viewportExtent = new Extent(
                viewportCenterX - worldWidth / 2,
                viewportCenterY - worldHeight / 2,
                viewportCenterX + worldWidth / 2,
                viewportCenterY + worldHeight / 2
            );

            // 3. Download and Draw Tiles
            using var surface = SKSurface.Create(new SKImageInfo(request.Width, request.Height));
            if (surface == null)
            {
                throw new InvalidOperationException("Failed to allocate Skia graphics surface. Memory may be constrained or image dimensions invalid.");
            }
            var canvas = surface.Canvas;

            // Render settings
            if (style.Preset == "dark")
            {
                canvas.Clear(SKColors.Black);
            }
            else if (style.Preset == "light")
            {
                canvas.Clear(new SKColor(245, 245, 247));
            }
            else
            {
                canvas.Clear(SKColors.White);
            }

            using (_perf.Measure("Render Tiles"))
            {
                var tileInfos = _tileSource.Schema.GetTileInfos(viewportExtent, levelId);
                var tileList = tileInfos.ToList();
                var tileResolution = _tileSource.Schema.Resolutions[levelId].UnitsPerPixel;
                var expectedCols = Math.Ceiling(viewportExtent.Width / (256.0 * tileResolution)) + 2;
                var expectedRows = Math.Ceiling(viewportExtent.Height / (256.0 * tileResolution)) + 2;
                var maxTilesLimit = Math.Max(500, (int)(expectedCols * expectedRows * 1.5));
                if (tileList.Count > maxTilesLimit)
                {
                    throw new ArgumentException(
                        $"The requested map area requires {tileList.Count} tiles, which exceeds the allowed limit of {maxTilesLimit}. " +
                        "Please decrease image dimensions, increase zoom level, or specify a more specific city/area.");
                }

                // Disable anti-alias on tile paint to prevent seam lines between adjacent raster tiles
                using var paint = new SKPaint
                {
                    IsAntialias = false
                };

                using var colorFilter = style.Preset switch
                {
                    "dark" => SKColorFilter.CreateColorMatrix(new float[]
                    {
                        -0.2126f, -0.7152f, -0.0722f, 0, 1.0f,
                        -0.2126f, -0.7152f, -0.0722f, 0, 1.0f,
                        -0.2126f, -0.7152f, -0.0722f, 0, 1.0f,
                         0,        0,        0,       1, 0
                    }),
                    "light" => SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, 64), SKBlendMode.SrcOver),
                    _ => null
                };

                if (colorFilter != null)
                {
                    paint.ColorFilter = colorFilter;
                }

                var loadedTiles = new ConcurrentBag<(SKImage Image, SKRect DestRect)>();
                try
                {
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount, 4),
                        CancellationToken = cancellationToken
                    };

                    await Parallel.ForEachAsync(tileList, parallelOptions, async (tileInfo, ct) =>
                    {
                        try
                        {
                            int z = tileInfo.Index.Level;
                            int tx = tileInfo.Index.Col;
                            int ty = tileInfo.Index.Row;

                            var tileBytes = await _tileCache.GetOrDownloadTileAsync(
                                z,
                                tx,
                                ty,
                                async downloadCt =>
                                {
                                    await _osmTileDownloadSemaphore.WaitAsync(downloadCt);
                                    try
                                    {
                                        var url = _tileOptions.Url
                                            .Replace("{z}", z.ToString(), StringComparison.Ordinal)
                                            .Replace("{x}", tx.ToString(), StringComparison.Ordinal)
                                            .Replace("{y}", ty.ToString(), StringComparison.Ordinal);

                                        using var response = await _externalHttp.SendAsync(
                                            ExternalHttpClientNames.Tiles,
                                            _tileOptions,
                                            () => new HttpRequestMessage(HttpMethod.Get, url),
                                            downloadCt);
                                        response.EnsureSuccessStatusCode();
                                        return await response.Content.ReadAsByteArrayAsync(downloadCt);
                                    }
                                    finally
                                    {
                                        _osmTileDownloadSemaphore.Release();
                                    }
                                },
                                ct);

                            if (tileBytes != null && tileBytes.Length > 0)
                            {
                                using var skData = SKData.CreateCopy(tileBytes);
                                using var codec = SKCodec.Create(skData);
                                var dimensionsAreSafe = codec != null
                                    && codec.Info.Width is > 0 and <= 1024
                                    && codec.Info.Height is > 0 and <= 1024
                                    && (long)codec.Info.Width * codec.Info.Height <= 1_048_576;
                                var image = dimensionsAreSafe ? SKImage.FromEncodedData(skData) : null;
                                if (image != null)
                                {
                                    var tileExtent = tileInfo.Extent;
                                    var left = (float)((tileExtent.MinX - viewportExtent.MinX) / resolution);
                                    var top = (float)((viewportExtent.MaxY - tileExtent.MaxY) / resolution);
                                    var width = (float)(tileExtent.Width / resolution);
                                    var height = (float)(tileExtent.Height / resolution);
                                    // Slightly expand tile bounds to eliminate subpixel seam lines between adjacent raster tiles
                                    var destRect = new SKRect(left, top, left + width + 0.5f, top + height + 0.5f);

                                    loadedTiles.Add((image, destRect));
                                }
                                else
                                {
                                    _logger.LogWarning("Tile data for z={Z}, x={X}, y={Y} failed Skia decoding, evicting corrupted cache entry.", z, tx, ty);
                                    _tileCache.EvictTile(z, tx, ty);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to load tile z={Z}, x={X}, y={Y}", tileInfo.Index.Level, tileInfo.Index.Col, tileInfo.Index.Row);
                        }
                    });

                    if (tileList.Count > 0 && loadedTiles.IsEmpty)
                    {
                        throw new ExternalServiceException("Tiles", "All map tiles failed to load from the tile provider.");
                    }

                    var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                    foreach (var (tileImage, destRect) in loadedTiles)
                    {
                        canvas.DrawImage(tileImage, destRect, sampling, paint);
                    }
                }
                finally
                {
                    foreach (var (tileImage, _) in loadedTiles)
                    {
                        tileImage.Dispose();
                    }
                }
            }

            // 4. Draw Streets
            using (_perf.Measure("Render Vectors"))
            {
                var highlightColorHex = !string.IsNullOrWhiteSpace(style.HighlightColor)
                    ? style.HighlightColor
                    : "#FF5733";

                if (!TryParseHexColor(highlightColorHex, out var baseColor))
                {
                    baseColor = new SKColor(255, 87, 51);
                }

                var opacity = Math.Clamp(style.Opacity, 0.0f, 1.0f);
                var strokeWidth = Math.Clamp(style.StrokeWidth, 0.1f, 50.0f);
                var finalAlpha = (byte)(baseColor.Alpha * opacity);

                using var streetPaint = new SKPaint
                {
                    Color = baseColor.WithAlpha(finalAlpha),
                    StrokeWidth = strokeWidth,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeCap = SKStrokeCap.Round, // Rounded ends
                    StrokeJoin = SKStrokeJoin.Round // Rounded corners
                };

                // Draw all segments in a single SKPath to avoid duplicate alpha overlap spots at joints
                using var pathBuilder = new SKPathBuilder();
                foreach (var path in streets)
                {
                    bool first = true;
                    foreach (var pt in path.Points)
                    {
                        var merc = ToSphericalMercator(pt.Lat, pt.Lon);
                        var x = (float)((merc.x - viewportExtent.MinX) / resolution);
                        var y = (float)((viewportExtent.MaxY - merc.y) / resolution);

                        if (first)
                        {
                            pathBuilder.MoveTo(x, y);
                            first = false;
                        }
                        else
                        {
                            pathBuilder.LineTo(x, y);
                        }
                    }
                }

                using var skPath = pathBuilder.Detach();
                canvas.DrawPath(skPath, streetPaint);
            }

            DrawAttribution(canvas, request.Width, request.Height);

            // 5. Save to Result
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
            {
                throw new InvalidOperationException("Failed to encode map image to PNG format.");
            }
            return data.ToArray();
        }

        private static void DrawAttribution(SKCanvas canvas, int imageWidth, int imageHeight)
        {
            string[] lines;
            if (imageWidth < 100)
            {
                lines = ["© Open", "StreetMap", "contri-", "butors"];
            }
            else if (imageWidth < 250)
            {
                lines = ["© OpenStreetMap", "contributors"];
            }
            else
            {
                lines = ["© OpenStreetMap contributors"];
            }

            var fontSize = Math.Clamp(Math.Min(imageWidth / 75f, imageHeight / 60f), 9f, 15f);
            using var font = new SKFont(AttributionTypeface.Value, fontSize)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true
            };
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            const float outerMargin = 3f;
            const float horizontalPadding = 3f;
            const float verticalPadding = 2f;
            var maxAllowedTextWidth = imageWidth - 2 * (outerMargin + horizontalPadding);
            var textWidth = lines.Max(line => font.MeasureText(line, textPaint));
            while (textWidth > maxAllowedTextWidth && font.Size > 4.5f)
            {
                font.Size -= 0.25f;
                textWidth = lines.Max(line => font.MeasureText(line, textPaint));
            }

            var lineHeight = Math.Max(font.Spacing, font.Size);
            var textHeight = lineHeight * lines.Length;
            var boxWidth = textWidth + horizontalPadding * 2;
            var boxHeight = textHeight + verticalPadding * 2;
            var box = new SKRect(
                imageWidth - outerMargin - boxWidth,
                imageHeight - outerMargin - boxHeight,
                imageWidth - outerMargin,
                imageHeight - outerMargin);

            using var backgroundPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 175),
                IsAntialias = true
            };
            canvas.DrawRoundRect(box, 3f, 3f, backgroundPaint);

            var baseline = box.Top + verticalPadding - font.Metrics.Ascent;
            foreach (var line in lines)
            {
                canvas.DrawText(
                    line,
                    box.Right - horizontalPadding,
                    baseline,
                    SKTextAlign.Right,
                    font,
                    textPaint);
                baseline += lineHeight;
            }
        }

        private static SKTypeface LoadAttributionTypeface()
        {
            using var fontStream = typeof(MapRendererService).Assembly
                .GetManifestResourceStream(AttributionFontResource)
                ?? throw new InvalidOperationException(
                    $"Embedded attribution font '{AttributionFontResource}' was not found.");
            return SKTypeface.FromStream(fontStream)
                ?? throw new InvalidOperationException("Failed to load the embedded attribution font.");
        }

        private static (double x, double y) ToSphericalMercator(double lat, double lon)
        {
            lat = Math.Clamp(lat, -85.05112878, 85.05112878);
            lon = Math.Clamp(lon, -180.0, 180.0);
            var x = lon * 20037508.342789244 / 180.0;
            var y = Math.Log(Math.Tan((90.0 + lat) * Math.PI / 360.0)) / (Math.PI / 180.0);
            y = y * 20037508.342789244 / 180.0;
            return (x, y);
        }

        private static bool TryParseHexColor(string? hex, out SKColor color)
        {
            color = SKColors.Empty;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim();
            if (s.StartsWith('#')) s = s[1..];

            byte r, g, b, a = 255;
            if (s.Length == 3)
            {
                if (!byte.TryParse(string.Concat(s[0], s[0]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                    !byte.TryParse(string.Concat(s[1], s[1]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                    !byte.TryParse(string.Concat(s[2], s[2]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    return false;
            }
            else if (s.Length == 4)
            {
                if (!byte.TryParse(string.Concat(s[0], s[0]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                    !byte.TryParse(string.Concat(s[1], s[1]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                    !byte.TryParse(string.Concat(s[2], s[2]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b) ||
                    !byte.TryParse(string.Concat(s[3], s[3]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
                    return false;
            }
            else if (s.Length == 6)
            {
                if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                    !byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                    !byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    return false;
            }
            else if (s.Length == 8)
            {
                if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                    !byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                    !byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b) ||
                    !byte.TryParse(s.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
                    return false;
            }
            else
            {
                return false;
            }

            color = new SKColor(r, g, b, a);
            return true;
        }
    }
}
