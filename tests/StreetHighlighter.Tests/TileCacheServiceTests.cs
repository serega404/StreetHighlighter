using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreetHighlighter.Configuration;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class TileCacheServiceTests
{
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "StreetHighlighter.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void IsValidPng_HeaderOnlyWithoutIend_ReturnsFalse()
    {
        byte[] headerOnly = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
        Assert.False(TileCacheService.IsValidPng(headerOnly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E })] // Truncated
    public void IsValidPng_NullOrShortData_ReturnsFalse(byte[]? data)
    {
        Assert.False(TileCacheService.IsValidPng(data));
    }

    [Fact]
    public void IsValidPng_HtmlOrTextData_ReturnsFalse()
    {
        byte[] html = Encoding.UTF8.GetBytes("<html><body>Error 404</body></html>");
        Assert.False(TileCacheService.IsValidPng(html));
    }

    [Fact]
    public void IsValidPng_TruncatedPngWithoutIend_ReturnsFalse()
    {
        // 30 bytes: has PNG header but cut off without IEND chunk
        byte[] truncated = new byte[30];
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Array.Copy(header, truncated, 8);
        Assert.False(TileCacheService.IsValidPng(truncated));
    }

    [Fact]
    public void IsValidPng_CompletePngWithIend_ReturnsTrue()
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] iend = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        byte[] complete = new byte[header.Length + 10 + iend.Length];
        Array.Copy(header, 0, complete, 0, header.Length);
        Array.Copy(iend, 0, complete, complete.Length - iend.Length, iend.Length);

        Assert.True(TileCacheService.IsValidPng(complete));
    }

    [Fact]
    public async Task SaveTileAsync_DifferentProviders_UseDifferentCacheNamespaces()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sh_tile_test_{Guid.NewGuid():N}");
        try
        {
            var env = new FakeWebHostEnvironment { ContentRootPath = tempDir };
            var first = CreateService(env, "https://first.example/{z}/{x}/{y}.png");
            var second = CreateService(env, "https://second.example/{z}/{x}/{y}.png");
            var tile = CreateStructurallyValidPng();

            await first.SaveTileAsync(1, 2, 3, tile);
            await second.SaveTileAsync(1, 2, 3, tile);

            var cachedFiles = Directory.GetFiles(
                Path.Combine(tempDir, "Cache", "Tiles"),
                "3.png",
                SearchOption.AllDirectories);
            Assert.Equal(2, cachedFiles.Length);
            Assert.Equal(2, cachedFiles.Select(Path.GetDirectoryName).Distinct().Count());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static TileCacheService CreateService(FakeWebHostEnvironment env, string tileUrl)
        => new(
            env,
            Options.Create(new ExternalServicesOptions
            {
                Tiles = new ExternalServiceOptions { Url = tileUrl }
            }),
            NullLogger<TileCacheService>.Instance);

    private static byte[] CreateStructurallyValidPng()
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] iend = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        byte[] complete = new byte[header.Length + 10 + iend.Length];
        Array.Copy(header, complete, header.Length);
        Array.Copy(iend, 0, complete, complete.Length - iend.Length, iend.Length);
        return complete;
    }
}
