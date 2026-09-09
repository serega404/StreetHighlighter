namespace StreetHighlighter.Services;

public class CacheCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxFileAge = TimeSpan.FromDays(14);
    private static readonly TimeSpan MaxTempFileAge = TimeSpan.FromHours(1);
    private const long MaxCacheSizeBytes = 1024L * 1024L * 1024L; // 1 GB
    private const long TargetCacheSizeBytes = 800L * 1024L * 1024L; // 800 MB

    private readonly string _cacheRoot;
    private readonly ILogger<CacheCleanupHostedService> _logger;

    public CacheCleanupHostedService(IWebHostEnvironment env, ILogger<CacheCleanupHostedService> logger)
    {
        _cacheRoot = Path.Combine(env.ContentRootPath, "Cache");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to avoid slowing down application startup
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunCleanup();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during cache cleanup cycle");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void RunCleanup()
    {
        if (!Directory.Exists(_cacheRoot)) return;

        var now = DateTime.UtcNow;
        var dirInfo = new DirectoryInfo(_cacheRoot);
        var allFiles = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var deletedCount = 0;
        long freedBytes = 0;
        var validFiles = new List<FileInfo>();

        // 1. Delete stale temp files (.tmp_*) and expired files older than MaxFileAge
        foreach (var file in allFiles)
        {
            if (file.Name.StartsWith(".tmp_", StringComparison.Ordinal))
            {
                if ((now - file.LastWriteTimeUtc) > MaxTempFileAge)
                {
                    try
                    {
                        var len = SafeGetFileLength(file);
                        file.Delete();
                        deletedCount++;
                        freedBytes += len;
                    }
                    catch { }

                    if (File.Exists(file.FullName))
                    {
                        validFiles.Add(file);
                    }
                }
                continue;
            }

            if ((now - file.LastWriteTimeUtc) > MaxFileAge)
            {
                var deleted = false;
                try
                {
                    var len = SafeGetFileLength(file);
                    file.Delete();
                    deletedCount++;
                    freedBytes += len;
                    deleted = true;
                }
                catch { }

                if (!deleted && File.Exists(file.FullName))
                {
                    validFiles.Add(file);
                }
                continue;
            }

            validFiles.Add(file);
        }

        // 2. Check total size and prune oldest if exceeding MaxCacheSizeBytes (LRU by LastWriteTimeUtc)
        var totalSize = validFiles.Sum(SafeGetFileLength);

        if (totalSize > MaxCacheSizeBytes)
        {
            _logger.LogInformation("Cache size ({SizeMb} MB) exceeds maximum limit ({MaxMb} MB). Pruning oldest files...",
                totalSize / (1024 * 1024), MaxCacheSizeBytes / (1024 * 1024));

            // TileCacheService updates LastWriteTimeUtc on cache access, so ordering by LastWriteTimeUtc implements true LRU
            var ordered = validFiles.OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var file in ordered)
            {
                if (totalSize <= TargetCacheSizeBytes) break;

                try
                {
                    var len = SafeGetFileLength(file);
                    file.Delete();
                    totalSize -= len;
                    deletedCount++;
                    freedBytes += len;
                }
                catch { }
            }
        }

        // Clean up empty directories, protecting system directories (Cache/Geo, Cache/Tiles)
        DeleteEmptyDirectories(_cacheRoot);

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cache cleanup finished: removed {Count} files, freed {Mb} MB",
                deletedCount, freedBytes / (1024 * 1024));
        }
    }

    private void DeleteEmptyDirectories(string startLocation)
    {
        var protectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_cacheRoot, "Geo"),
            Path.Combine(_cacheRoot, "Tiles")
        };

        DeleteEmptyDirectoriesInternal(startLocation, protectedDirs);
    }

    private static void DeleteEmptyDirectoriesInternal(string startLocation, HashSet<string> protectedDirs)
    {
        if (!Directory.Exists(startLocation)) return;

        foreach (var directory in Directory.GetDirectories(startLocation))
        {
            DeleteEmptyDirectoriesInternal(directory, protectedDirs);
            if (!protectedDirs.Contains(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                try { Directory.Delete(directory, false); } catch { }
            }
        }
    }

    private static long SafeGetFileLength(FileInfo file)
    {
        try
        {
            file.Refresh();
            return file.Exists ? file.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
