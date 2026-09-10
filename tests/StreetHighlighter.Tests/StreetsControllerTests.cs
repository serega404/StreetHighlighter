using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using StreetHighlighter.Controllers;
using StreetHighlighter.Models;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class StreetsControllerTests
{
    private sealed class FakeGeoDataService : IGeoDataService
    {
        public CityInfo? CityInfo { get; set; } = new(
            new GeoBounds(47.1, 38.7, 47.3, 39.0),
            "Таганрог",
            "Таганрог, Ростовская область, Россия");

        public List<string> StreetNames { get; set; } = ["улица Инициативная", "улица Морозова"];
        public Exception? Exception { get; set; }

        public Task<GeoBounds?> GetCityBoundsAsync(string cityName, CancellationToken cancellationToken = default)
            => Task.FromResult(CityInfo?.Bounds);

        public Task<CityInfo?> GetCityInfoAsync(string cityName, CancellationToken cancellationToken = default)
        {
            if (Exception != null) throw Exception;
            return Task.FromResult(CityInfo);
        }

        public Task<List<string>> GetStreetNamesAsync(
            string cityName,
            GeoBounds bounds,
            CancellationToken cancellationToken = default)
            => Task.FromResult(StreetNames);

        public Task<List<GeoPath>> GetStreetGeometryAsync(
            string cityName,
            List<string> streets,
            GeoBounds? bounds = null,
            bool exactStreetNames = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<GeoPath>());
    }

    [Fact]
    public async Task GetStreets_ReturnsCityVariantsAndCanonicalStreetNames()
    {
        var controller = CreateController(new FakeGeoDataService());

        var result = await controller.GetStreets(" taganrog ");

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<StreetsResponse>(ok.Value);
        Assert.Equal("Таганрог", response.CityName);
        Assert.Equal(
            ["taganrog", "Таганрог", "Таганрог, Ростовская область, Россия"],
            response.CityNames);
        Assert.Equal(["улица Инициативная", "улица Морозова"], response.Streets);
    }

    [Fact]
    public async Task GetStreets_CityNotFound_Returns404()
    {
        var controller = CreateController(new FakeGeoDataService { CityInfo = null });

        var result = await controller.GetStreets("Неизвестный город");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task GetStreets_ExternalServiceFailure_Returns502()
    {
        var controller = CreateController(new FakeGeoDataService
        {
            Exception = new ExternalServiceException("Overpass", "Server error 503")
        });

        var result = await controller.GetStreets("Таганрог");

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
    }

    private static StreetsController CreateController(IGeoDataService geoData)
    {
        var controller = new StreetsController(geoData, NullLogger<StreetsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }
}
