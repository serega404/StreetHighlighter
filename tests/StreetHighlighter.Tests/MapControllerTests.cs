using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using StreetHighlighter.Controllers;
using StreetHighlighter.Models;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class MapControllerTests
{
    private class FakeRenderer : IMapRendererService
    {
        public Func<MapRequest, Task<byte[]>>? OnGenerate { get; set; }

        public Task<byte[]> GenerateMapAsync(MapRequest request, string requestId, CancellationToken cancellationToken = default)
        {
            if (OnGenerate != null) return OnGenerate(request);
            return Task.FromResult(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        }
    }

    [Fact]
    public async Task GenerateMap_NullStyle_DoesNotThrowNullReference()
    {
        var renderer = new FakeRenderer();
        var controller = new MapController(renderer, NullLogger<MapController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var request = new MapRequest
        {
            CityName = "Таганрог",
            Style = null!
        };

        var result = await controller.GenerateMap(request);

        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(request.Style);
    }

    [Fact]
    public async Task GenerateMap_CityNotFound_Returns404()
    {
        var renderer = new FakeRenderer
        {
            OnGenerate = _ => throw new CityNotFoundException()
        };
        var controller = new MapController(renderer, NullLogger<MapController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var request = new MapRequest { CityName = "НесуществующийГород123" };
        var result = await controller.GenerateMap(request);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task GenerateMap_ExternalServiceException_Returns502()
    {
        var renderer = new FakeRenderer
        {
            OnGenerate = _ => throw new ExternalServiceException("Nominatim", "Server error 503")
        };
        var controller = new MapController(renderer, NullLogger<MapController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var request = new MapRequest { CityName = "Москва" };
        var result = await controller.GenerateMap(request);

        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GenerateMap_DownloadFlag_SetsSafeFilename()
    {
        var renderer = new FakeRenderer();
        var controller = new MapController(renderer, NullLogger<MapController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var request = new MapRequest { CityName = "Таганрог" };
        var result = await controller.GenerateMap(request, download: true);

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Contains("Таганрог", fileResult.FileDownloadName);
        Assert.EndsWith(".png", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task GetMap_ValidParameters_ReturnsImageResult()
    {
        MapRequest? capturedRequest = null;
        var renderer = new FakeRenderer
        {
            OnGenerate = req =>
            {
                capturedRequest = req;
                return Task.FromResult(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            }
        };

        var controller = new MapController(renderer, NullLogger<MapController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.GetMap(
            city: "Москва",
            streets: "Арбат, Тверская",
            preset: "dark",
            width: 1000,
            height: 800,
            zoom: 14,
            offsetX: 120,
            offsetY: -80);

        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Москва", capturedRequest.CityName);
        Assert.Equal(2, capturedRequest.HighlightStreets.Count);
        Assert.Equal("Арбат", capturedRequest.HighlightStreets[0]);
        Assert.Equal("Тверская", capturedRequest.HighlightStreets[1]);
        Assert.Equal("dark", capturedRequest.Style.Preset);
        Assert.Equal(1000, capturedRequest.Width);
        Assert.Equal(800, capturedRequest.Height);
        Assert.Equal(14, capturedRequest.Zoom);
        Assert.Equal(120, capturedRequest.OffsetX);
        Assert.Equal(-80, capturedRequest.OffsetY);
        Assert.Equal("public, max-age=3600", httpContext.Response.Headers.CacheControl.ToString());
    }
}
