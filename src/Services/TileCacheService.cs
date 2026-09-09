using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StreetHighlighter.Configuration;

namespace StreetHighlighter.Services
{
    public interface ITileCacheService
    {
        Task<byte[]?> GetCachedTileAsync(int z, int x, int y, CancellationToken cancellationToken = default);
        Task SaveTileAsync(
            int z,
            int x,
            int y,
            byte[] tileData,
            CancellationToken cancellationToken = default);
        Task<byte[]?> GetOrDownloadTileAsync(
            int z,
            int x,
            int y,
            Func<CancellationToken, Task<byte[]>> downloadFactory,
            CancellationToken cancellationToken = default);
        void EvictTile(int z, int x, int y);
    }

    public class TileCacheService : ITileCacheService
    {
        private readonly string _cacheDirectory;
        private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlightDownloads = new();
        private readonly ILogger<TileCacheService> _logger;

        public TileCacheService(
            IWebHostEnvironment env,
            IOptions<ExternalServicesOptions> externalServices,
            ILogger<TileCacheService> logger)
        {
            _logger = logger;
            var providerKey = GetProviderCacheKey(externalServices.Value.Tiles.Url);
            _cacheDirectory = Path.Combine(env.ContentRootPath, "Cache", "Tiles", providerKey);

            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }

        internal static string GetProviderCacheKey(string tileUrl)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tileUrl.Trim()));
            return Convert.ToHexString(hash.AsSpan(0, 12));
        }

        private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        private static readonly byte[] PngIendChunk = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

        public static bool IsValidPng(byte[]? data)
        {
            if (data == null || data.Length < 20) return false;
            if (!data.AsSpan(0, 8).SequenceEqual(PngHeader)) return false;
            if (!data.AsSpan(data.Length - 12, 12).SequenceEqual(PngIendChunk)) return false;
            return true;
        }

        public async Task<byte[]?> GetCachedTileAsync(
            int z,
            int x,
            int y,
            CancellationToken cancellationToken = default)
        {
            var path = GetFilePath(z, x, y);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var data = await File.ReadAllBytesAsync(path, cancellationToken);
                if (!IsValidPng(data))
                {
                    _logger.LogWarning("Corrupted or non-PNG tile detected at {Path}, deleting cache entry.", path);
                    try { File.Delete(path); } catch { }
                    return null;
                }

                // Touch the file on read so active cache entries are not pruned by LRU / age cleanup
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }

                return data;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read cached tile at {Path}", path);
                return null;
            }
        }

        public void EvictTile(int z, int x, int y)
        {
            var path = GetFilePath(z, x, y);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("Evicted corrupted or invalid tile from cache: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evict tile at {Path}", path);
            }
        }

        public async Task SaveTileAsync(
            int z,
            int x,
            int y,
            byte[] tileData,
            CancellationToken cancellationToken = default)
        {
            if (!IsValidPng(tileData))
            {
                _logger.LogWarning("Refusing to cache invalid tile data ({Bytes} bytes) for z={Z}, x={X}, y={Y}", tileData?.Length ?? 0, z, x, y);
                return;
            }

            var path = GetFilePath(z, x, y);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = Path.Combine(dir ?? _cacheDirectory, $".tmp_{Guid.NewGuid():N}.png");
            try
            {
                await File.WriteAllBytesAsync(tempPath, tileData, cancellationToken);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another thread has concurrently written the tile
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save tile to cache {Path}", path);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        public async Task<byte[]?> GetOrDownloadTileAsync(
            int z,
            int x,
            int y,
            Func<CancellationToken, Task<byte[]>> downloadFactory,
            CancellationToken cancellationToken = default)
        {
            var cached = await GetCachedTileAsync(z, x, y, cancellationToken);
            if (cached != null)
            {
                return cached;
            }

            var key = $"{z}/{x}/{y}";

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_inFlightDownloads.TryGetValue(key, out var existingTask))
                {
                    try
                    {
                        return await existingTask.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The in-flight download was cancelled by the original requester.
                        // Since our token is still active, retry in the loop.
                        continue;
                    }
                }

                var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_inFlightDownloads.TryAdd(key, tcs.Task))
                {
                    try
                    {
                        var recheck = await GetCachedTileAsync(z, x, y, cancellationToken);
                        if (recheck != null)
                        {
                            tcs.TrySetResult(recheck);
                            return recheck;
                        }

                        var tileBytes = await downloadFactory(cancellationToken);
                        if (IsValidPng(tileBytes))
                        {
                            await SaveTileAsync(z, x, y, tileBytes, cancellationToken);
                            tcs.TrySetResult(tileBytes);
                            return tileBytes;
                        }

                        _logger.LogWarning("Downloaded tile data for z={Z}, x={X}, y={Y} is invalid PNG.", z, x, y);
                        tcs.TrySetResult(null);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                        throw;
                    }
                    finally
                    {
                        _inFlightDownloads.TryRemove(key, out _);
                    }
                }
            }
        }

        private string GetFilePath(int z, int x, int y)
        {
            // Structure: Cache/Tiles/{z}/{x}/{y}.png
            return Path.Combine(_cacheDirectory, $"{z}", $"{x}", $"{y}.png");
        }
    }
}
