using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using StreetHighlighter.Models;
using StreetHighlighter.Services;

namespace StreetHighlighter.Controllers;

[ApiController]
[Route("api/v1/streets")]
public sealed class StreetsController : ControllerBase
{
    private readonly IGeoDataService _geoData;
    private readonly ILogger<StreetsController> _logger;

    public StreetsController(IGeoDataService geoData, ILogger<StreetsController> logger)
    {
        _geoData = geoData;
        _logger = logger;
    }

    /// <summary>
    /// Returns unique canonical OSM street names within a city's bounding box.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(StreetsResponse), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest, "application/json")]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound, "application/json")]
    [ProducesResponseType(typeof(object), StatusCodes.Status502BadGateway, "application/json")]
    [ProducesResponseType(typeof(object), StatusCodes.Status504GatewayTimeout, "application/json")]
    [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError, "application/json")]
    public async Task<IActionResult> GetStreets(
        [FromQuery, Required(ErrorMessage = "City is required.")]
        [StringLength(100, MinimumLength = 1, ErrorMessage = "City must be between 1 and 100 characters.")]
        string city)
    {
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            var query = city.Trim();
            _logger.LogInformation("Received street list request {RequestId} for city {City}", requestId, query);

            var cityInfo = await _geoData.GetCityInfoAsync(query, HttpContext.RequestAborted);
            if (cityInfo == null)
            {
                throw new CityNotFoundException();
            }

            var streets = await _geoData.GetStreetNamesAsync(
                query,
                cityInfo.Bounds,
                HttpContext.RequestAborted);

            var cityNames = new[] { query, cityInfo.Name, cityInfo.DisplayName }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(new StreetsResponse(cityInfo.Name, cityNames, streets));
        }
        catch (CityNotFoundException ex)
        {
            _logger.LogWarning(ex, "City not found: {City} (RequestId: {RequestId})", city, requestId);
            return NotFound(new { Error = ex.Message });
        }
        catch (ExternalServiceException ex)
        {
            _logger.LogWarning(ex, "External service error for street list request {RequestId}: {Message}", requestId, ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error loading street list for request {RequestId}", requestId);
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = "Failed to communicate with external map services: " + ex.Message });
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "External service timed out for street list request {RequestId}", requestId);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = "A timeout occurred while communicating with external map services. Please try again." });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Street list request {RequestId} was cancelled by the client", requestId);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error loading street list for request {RequestId}", requestId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred while loading street names. Please check server logs." });
        }
    }
}
