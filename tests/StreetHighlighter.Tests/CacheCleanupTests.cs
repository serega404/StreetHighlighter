using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class CacheCleanupTests
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

    [Fact]
    public void RunCleanup_PreservesGeoAndTilesDirectories_EvenWhenEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sh_test_{Guid.NewGuid():N}");
        try
        {
            var cacheRoot = Path.Combine(tempDir, "Cache");
            var geoDir = Path.Combine(cacheRoot, "Geo");
            var tilesDir = Path.Combine(cacheRoot, "Tiles");
            var emptySubDir = Path.Combine(tilesDir, "14", "25");

            Directory.CreateDirectory(geoDir);
            Directory.CreateDirectory(emptySubDir);

            var env = new FakeWebHostEnvironment { ContentRootPath = tempDir };
            var service = new CacheCleanupHostedService(env, NullLogger<CacheCleanupHostedService>.Instance);

            service.RunCleanup();

            // Cache/Geo and Cache/Tiles MUST NOT be deleted
            Assert.True(Directory.Exists(geoDir), "Cache/Geo directory was erroneously deleted.");
            Assert.True(Directory.Exists(tilesDir), "Cache/Tiles directory was erroneously deleted.");

            // Empty sub-subdirectories like Cache/Tiles/14/25 should be cleaned up
            Assert.False(Directory.Exists(emptySubDir), "Empty nested directory was not cleaned up.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
