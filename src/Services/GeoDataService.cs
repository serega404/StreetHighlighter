using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreetHighlighter.Configuration;
using Microsoft.Extensions.Options;

namespace StreetHighlighter.Services
{
    public readonly record struct GeoPoint(double Lat, double Lon);
    public record GeoBounds(double MinLat, double MinLon, double MaxLat, double MaxLon);
    public record GeoPath(List<GeoPoint> Points);

    public interface IGeoDataService
    {
        Task<GeoBounds?> GetCityBoundsAsync(string cityName, CancellationToken cancellationToken = default);
        Task<List<GeoPath>> GetStreetGeometryAsync(
            string cityName,
            List<string> streets,
            GeoBounds? bounds = null,
            CancellationToken cancellationToken = default);
    }

    public class GeoDataService : IGeoDataService
    {
        private static readonly TimeSpan BoundsCacheLifetime = TimeSpan.FromDays(7);
        private static readonly TimeSpan StreetsCacheLifetime = TimeSpan.FromDays(1);
        private const int MaxStreetPaths = 50_000;
        private const int MaxStreetPoints = 1_000_000;
        private static readonly ConcurrentDictionary<string, Task<GeoBounds?>> _inFlightBounds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<List<GeoPath>>> _inFlightStreets = new(StringComparer.Ordinal);
        private static readonly SemaphoreSlim _nominatimThrottle = new(1, 1);
        private static readonly SemaphoreSlim _overpassThrottle = new(2, 2);
        private static DateTime _lastNominatimRequestUtc = DateTime.MinValue;

        private readonly IExternalHttpService _externalHttp;
        private readonly ExternalServicesOptions _externalServices;
        private readonly ILogger<GeoDataService> _logger;
        private readonly string _cacheDir;

        public GeoDataService(
            IExternalHttpService externalHttp,
            IOptions<ExternalServicesOptions> externalServices,
            IWebHostEnvironment env,
            ILogger<GeoDataService> logger)
        {
            _externalHttp = externalHttp;
            _externalServices = externalServices.Value;
            _logger = logger;

            _cacheDir = Path.Combine(env.ContentRootPath, "Cache", "Geo");
            if (!Directory.Exists(_cacheDir))
            {
                Directory.CreateDirectory(_cacheDir);
            }
        }

        public async Task<GeoBounds?> GetCityBoundsAsync(
            string cityName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(cityName)) return null;

            var normalizedCity = cityName.Trim().ToLowerInvariant();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_inFlightBounds.TryGetValue(normalizedCity, out var existingTask))
                {
                    try
                    {
                        return await existingTask.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // In-flight task was cancelled by previous requester; retry in loop
                        continue;
                    }
                }

                var tcs = new TaskCompletionSource<GeoBounds?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_inFlightBounds.TryAdd(normalizedCity, tcs.Task))
                {
                    try
                    {
                        var result = await GetCityBoundsInternalAsync(cityName, cancellationToken);
                        tcs.TrySetResult(result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                        throw;
                    }
                    finally
                    {
                        _inFlightBounds.TryRemove(normalizedCity, out _);
                    }
                }
            }
        }

        private async Task<GeoBounds?> GetCityBoundsInternalAsync(
            string cityName,
            CancellationToken cancellationToken)
        {
            var cacheFile = Path.Combine(_cacheDir, GetBoundsCacheFileName(cityName));

            // Try load from positive cache
            if (File.Exists(cacheFile))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) >= BoundsCacheLifetime)
                    {
                        TryDeleteFile(cacheFile);
                    }
                    else
                    {
                        GeoBounds? cached = null;
                        await using (var fs = File.OpenRead(cacheFile))
                        {
                            cached = await JsonSerializer.DeserializeAsync<GeoBounds>(fs, cancellationToken: cancellationToken);
                        }
                        if (cached != null)
                        {
                            return cached;
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to read bounds cache due to IO error for {City}", cityName);
                    TryDeleteFile(cacheFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize bounds cache for {City}", cityName);
                    TryDeleteFile(cacheFile);
                }
            }

            // Check negative cache (recent non-existent city queries within 24h)
            var notFoundFile = Path.Combine(_cacheDir, GetNotFoundCacheFileName(cityName));
            if (File.Exists(notFoundFile))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(notFoundFile);
                    if (age < TimeSpan.FromHours(24))
                    {
                        _logger.LogInformation("Negative bounds cache hit for {City}", cityName);
                        return null;
                    }
                    TryDeleteFile(notFoundFile);
                }
                catch { }
            }

            var separator = _externalServices.Nominatim.Url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var url = $"{_externalServices.Nominatim.Url}{separator}q={Uri.EscapeDataString(cityName)}&format=json&limit=5&addressdetails=1";

            await _nominatimThrottle.WaitAsync(cancellationToken);
            HttpResponseMessage response;
            try
            {
                var elapsed = DateTime.UtcNow - _lastNominatimRequestUtc;
                if (elapsed < TimeSpan.FromMilliseconds(1100))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1100) - elapsed, cancellationToken);
                }
                _lastNominatimRequestUtc = DateTime.UtcNow;

                response = await _externalHttp.SendAsync(
                    ExternalHttpClientNames.Nominatim,
                    _externalServices.Nominatim,
                    () => new HttpRequestMessage(HttpMethod.Get, url),
                    cancellationToken);
            }
            finally
            {
                _nominatimThrottle.Release();
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Nominatim HTTP error: {StatusCode}", response.StatusCode);
                    throw new ExternalServiceException("Nominatim", $"Server returned HTTP {(int)response.StatusCode}");
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var doc = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc is not JsonArray results || results.Count == 0)
                {
                    await SaveNotFoundBoundsCacheAsync(notFoundFile, cancellationToken);
                    return null;
                }

                // If multiple results, prefer settlement/boundary over a point/shop POI
                JsonNode? selectedResult = null;
                foreach (var item in results)
                {
                    var osmClass = item?["class"]?.ToString();
                    var addresstype = item?["addresstype"]?.ToString();
                    if (osmClass == "boundary" || osmClass == "place" ||
                        addresstype is "city" or "town" or "village" or "municipality" or "administrative")
                    {
                        selectedResult = item;
                        break;
                    }
                }
                selectedResult ??= results[0];

                if (selectedResult?["boundingbox"] is not JsonArray bbox || bbox.Count != 4)
                {
                    await SaveNotFoundBoundsCacheAsync(notFoundFile, cancellationToken);
                    return null;
                }

                if (!double.TryParse(bbox[0]?.ToString(), CultureInfo.InvariantCulture, out var minLat) ||
                    !double.TryParse(bbox[2]?.ToString(), CultureInfo.InvariantCulture, out var minLon) ||
                    !double.TryParse(bbox[1]?.ToString(), CultureInfo.InvariantCulture, out var maxLat) ||
                    !double.TryParse(bbox[3]?.ToString(), CultureInfo.InvariantCulture, out var maxLon))
                {
                    await SaveNotFoundBoundsCacheAsync(notFoundFile, cancellationToken);
                    return null;
                }

                // buffer as strings: "boundingbox": ["minlat", "maxlat", "minlon", "maxlon"]
                var bounds = new GeoBounds(minLat, minLon, maxLat, maxLon);

                // Validate coordinate sanity
                if (double.IsNaN(bounds.MinLat) || double.IsNaN(bounds.MaxLat) ||
                    double.IsNaN(bounds.MinLon) || double.IsNaN(bounds.MaxLon) ||
                    bounds.MinLat < -90 || bounds.MaxLat > 90 ||
                    bounds.MinLon < -180 || bounds.MaxLon > 180 ||
                    bounds.MinLat > bounds.MaxLat)
                {
                    _logger.LogWarning("Nominatim returned invalid coordinate bounds for city {City}", cityName);
                    await SaveNotFoundBoundsCacheAsync(notFoundFile, cancellationToken);
                    return null;
                }

                // Save to cache atomically
                await SaveBoundsToCacheAsync(cacheFile, bounds, cancellationToken);

                return bounds;
            }
        }

        private async Task SaveBoundsToCacheAsync(string cacheFile, GeoBounds bounds, CancellationToken cancellationToken)
        {
            EnsureCacheDirectoryExists();
            var tempFile = Path.Combine(_cacheDir, $".tmp_{Guid.NewGuid():N}.json");
            try
            {
                await using (var fs = File.Create(tempFile))
                {
                    await JsonSerializer.SerializeAsync(fs, bounds, cancellationToken: cancellationToken);
                }
                File.Move(tempFile, cacheFile, overwrite: true);
            }
            catch (IOException) when (File.Exists(cacheFile))
            {
                // Concurrently written by another thread
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write bounds cache");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        public async Task<List<GeoPath>> GetStreetGeometryAsync(
            string cityName,
            List<string> streets,
            GeoBounds? bounds = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(cityName)) return new List<GeoPath>();

            var validStreets = streets?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList() ?? new List<string>();

            if (!validStreets.Any()) return new List<GeoPath>();

            var normalizedCity = cityName.Trim().ToLowerInvariant();
            var boundsKey = bounds != null
                ? $"_{bounds.MinLat.ToString("F3", CultureInfo.InvariantCulture)},{bounds.MinLon.ToString("F3", CultureInfo.InvariantCulture)},{bounds.MaxLat.ToString("F3", CultureInfo.InvariantCulture)},{bounds.MaxLon.ToString("F3", CultureInfo.InvariantCulture)}"
                : string.Empty;
            var normalizedStreetsKey = string.Join("|", validStreets.Select(s => s.ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));
            var key = CreateMd5($"{normalizedCity}{boundsKey}_{normalizedStreetsKey}");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_inFlightStreets.TryGetValue(key, out var existingTask))
                {
                    try
                    {
                        return await existingTask.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // In-flight task was cancelled by previous requester; retry in loop
                        continue;
                    }
                }

                var tcs = new TaskCompletionSource<List<GeoPath>>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_inFlightStreets.TryAdd(key, tcs.Task))
                {
                    try
                    {
                        var result = await GetStreetGeometryInternalAsync(cityName, validStreets, bounds, key, cancellationToken);
                        tcs.TrySetResult(result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                        throw;
                    }
                    finally
                    {
                        _inFlightStreets.TryRemove(key, out _);
                    }
                }
            }
        }

        private async Task<List<GeoPath>> GetStreetGeometryInternalAsync(
            string cityName,
            List<string> validStreets,
            GeoBounds? bounds,
            string key,
            CancellationToken cancellationToken)
        {
            var cacheFile = Path.Combine(_cacheDir, $"streets_{key}.json");

            // Try load from cache
            if (File.Exists(cacheFile))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) >= StreetsCacheLifetime)
                    {
                        TryDeleteFile(cacheFile);
                    }
                    else
                    {
                        List<GeoPath>? cached = null;
                        await using (var fs = File.OpenRead(cacheFile))
                        {
                            cached = await JsonSerializer.DeserializeAsync<List<GeoPath>>(
                                fs,
                                cancellationToken: cancellationToken);
                        }
                        if (cached != null)
                        {
                            return cached;
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to read streets cache due to IO error for key {Key}", key);
                    TryDeleteFile(cacheFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize streets cache for key {Key}", key);
                    TryDeleteFile(cacheFile);
                }
            }

            // Construct Overpass QL query safely with combined regex
            var timeoutSeconds = Math.Clamp(_externalServices.Overpass.TimeoutSeconds, 5, 300);

            string streetQueryPart;
            string areaPart;
            var combinedRegex = string.Join("|", validStreets.Select(EscapeOverpassRegex));

            if (bounds != null)
            {
                var minLat = bounds.MinLat.ToString(CultureInfo.InvariantCulture);
                var minLon = bounds.MinLon.ToString(CultureInfo.InvariantCulture);
                var maxLat = bounds.MaxLat.ToString(CultureInfo.InvariantCulture);
                var maxLon = bounds.MaxLon.ToString(CultureInfo.InvariantCulture);

                areaPart = string.Empty;
                streetQueryPart = $"way[\"highway\"][\"name\"~\"({combinedRegex})\",i]({minLat},{minLon},{maxLat},{maxLon});";
            }
            else
            {
                var escapedCity = EscapeOverpassStringLiteral(cityName.Trim());
                areaPart = $@"area[""name""=""{escapedCity}""]->.a;";
                streetQueryPart = $"way[\"highway\"][\"name\"~\"({combinedRegex})\",i](area.a);";
            }

            var query = $@"
                [out:json][timeout:{timeoutSeconds}][maxsize:{_externalServices.Overpass.MaxResponseBytes}];
                {areaPart}
                (
                  {streetQueryPart}
                );
                out geom;
            ";

            try
            {
                await _overpassThrottle.WaitAsync(cancellationToken);
                HttpResponseMessage response;
                try
                {
                    response = await _externalHttp.SendAsync(
                        ExternalHttpClientNames.Overpass,
                        _externalServices.Overpass,
                        () => new HttpRequestMessage(HttpMethod.Post, _externalServices.Overpass.Url)
                        {
                            Content = new FormUrlEncodedContent(new[]
                            {
                                new KeyValuePair<string, string>("data", query)
                            })
                        },
                        cancellationToken);
                }
                finally
                {
                    _overpassThrottle.Release();
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Overpass API error: {StatusCode}", response.StatusCode);
                        throw new ExternalServiceException("Overpass", $"Server returned HTTP {(int)response.StatusCode}");
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var doc = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc?["remark"] != null)
                    {
                        var remark = doc["remark"]?.ToString() ?? "Unknown Overpass error remark";
                        _logger.LogWarning("Overpass returned error remark: {Remark}", remark);
                        throw new ExternalServiceException("Overpass", remark);
                    }

                    var elements = doc?["elements"]?.AsArray();
                    var paths = new List<GeoPath>();
                    var totalPoints = 0;

                    if (elements != null)
                    {
                        foreach (var element in elements)
                        {
                            if (element?["geometry"] is JsonArray geometry)
                            {
                                var points = new List<GeoPoint>();
                                foreach (var pt in geometry)
                                {
                                    if (TryGetCoordinate(pt?["lat"], out var lat) &&
                                        TryGetCoordinate(pt?["lon"], out var lon))
                                    {
                                        points.Add(new GeoPoint(lat, lon));
                                        totalPoints++;
                                        if (totalPoints > MaxStreetPoints)
                                        {
                                            throw new ExternalServiceException(
                                                "Overpass",
                                                $"Response contains more than {MaxStreetPoints} geometry points.");
                                        }
                                    }
                                }
                                if (points.Count > 0)
                                {
                                    paths.Add(new GeoPath(points));
                                    if (paths.Count > MaxStreetPaths)
                                    {
                                        throw new ExternalServiceException(
                                            "Overpass",
                                            $"Response contains more than {MaxStreetPaths} street paths.");
                                    }
                                }
                            }
                        }
                    }

                    // Save to cache atomically
                    await SaveStreetsToCacheAsync(cacheFile, paths, cancellationToken);

                    return paths;
                }
            }
            catch (ExternalServiceException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching streets from Overpass");
                throw new ExternalServiceException("Overpass", "Failed to query or parse street geometries", ex);
            }
        }

        private async Task SaveStreetsToCacheAsync(string cacheFile, List<GeoPath> paths, CancellationToken cancellationToken)
        {
            EnsureCacheDirectoryExists();
            var tempFile = Path.Combine(_cacheDir, $".tmp_{Guid.NewGuid():N}.json");
            try
            {
                await using (var fs = File.Create(tempFile))
                {
                    await JsonSerializer.SerializeAsync(fs, paths, cancellationToken: cancellationToken);
                }
                File.Move(tempFile, cacheFile, overwrite: true);
            }
            catch (IOException) when (File.Exists(cacheFile))
            {
                // Concurrently written by another thread
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write streets cache");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private void EnsureCacheDirectoryExists()
        {
            if (!Directory.Exists(_cacheDir))
            {
                Directory.CreateDirectory(_cacheDir);
            }
        }

        private static string GetNotFoundCacheFileName(string cityName)
        {
            var normalized = cityName.Trim().ToLowerInvariant();
            var hash = CreateMd5(normalized);
            return $"bounds_notfound_{hash}.json";
        }

        private async Task SaveNotFoundBoundsCacheAsync(string notFoundFile, CancellationToken cancellationToken)
        {
            try
            {
                EnsureCacheDirectoryExists();
                await File.WriteAllTextAsync(notFoundFile, DateTime.UtcNow.ToString("O"), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save negative bounds cache");
            }
        }

        private static bool TryGetCoordinate(JsonNode? node, out double value)
        {
            value = 0;
            if (node == null) return false;
            if (node is JsonValue jv)
            {
                if (jv.TryGetValue<double>(out value)) return true;
                if (jv.TryGetValue<string>(out var str) && double.TryParse(str, CultureInfo.InvariantCulture, out value)) return true;
            }
            return false;
        }

        private static string GetBoundsCacheFileName(string cityName)
        {
            var normalized = cityName.Trim().ToLowerInvariant();
            var hash = CreateMd5(normalized);
            var safeChars = normalized
                .Where(char.IsAsciiLetterOrDigit)
                .Take(24)
                .ToArray();
            var prefix = safeChars.Length > 0 ? new string(safeChars) + "_" : string.Empty;
            return $"bounds_{prefix}{hash}.json";
        }

        private static string EscapeOverpassStringLiteral(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "")
                .Replace("\n", " ");
        }

        private static string EscapeOverpassRegex(string value)
        {
            var regexEscaped = System.Text.RegularExpressions.Regex.Escape(value);
            return EscapeOverpassStringLiteral(regexEscaped);
        }

        private static string CreateMd5(string input)
        {
            using var md5 = MD5.Create();
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);
            return Convert.ToHexString(hashBytes);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore failure on deleting corrupted cache
            }
        }
    }
}
