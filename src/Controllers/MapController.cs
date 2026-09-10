using StreetHighlighter.Models;
using StreetHighlighter.Services;
using Microsoft.AspNetCore.Mvc;

namespace StreetHighlighter.Controllers
{
    [ApiController]
    [Route("api/v1/map")]
    public class MapController : ControllerBase
    {
        private readonly IMapRendererService _renderer;
        private readonly ILogger<MapController> _logger;

        public MapController(IMapRendererService renderer, ILogger<MapController> logger)
        {
            _renderer = renderer;
            _logger = logger;
        }

        /// <summary>
        /// Generates a custom map image based on OSM data.
        /// </summary>
        /// <param name="request">Map generation parameters including city, style, and highlights.</param>
        /// <param name="download">If true, returns Content-Disposition attachment header.</param>
        /// <returns>A PNG image of the map.</returns>
        [HttpPost]
        [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "image/png")]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status502BadGateway, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status504GatewayTimeout, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError, "application/json")]
        public async Task<IActionResult> GenerateMap([FromBody] MapRequest request, [FromQuery] bool download = false)
        {
            if (request == null)
            {
                return BadRequest(new { Error = "Request body cannot be null." });
            }

            request.Style ??= new StyleSettings();

            // Ensure full validation including nested style settings
            var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var validationContext = new System.ComponentModel.DataAnnotations.ValidationContext(request);
            if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(request, validationContext, validationResults, validateAllProperties: true))
            {
                foreach (var result in validationResults)
                {
                    var memberName = result.MemberNames.FirstOrDefault() ?? string.Empty;
                    ModelState.AddModelError(memberName, result.ErrorMessage ?? "Invalid parameter.");
                }
            }

            var styleResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var styleContext = new System.ComponentModel.DataAnnotations.ValidationContext(request.Style);
            if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(request.Style, styleContext, styleResults, validateAllProperties: true))
            {
                foreach (var result in styleResults)
                {
                    var memberName = "Style." + (result.MemberNames.FirstOrDefault() ?? string.Empty);
                    ModelState.AddModelError(memberName, result.ErrorMessage ?? "Invalid style parameter.");
                }
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var requestId = Guid.NewGuid().ToString("N");
            try
            {
                _logger.LogInformation("Received map request {RequestId} for city {City}", requestId, request.CityName);

                var imageBytes = await _renderer.GenerateMapAsync(
                    request,
                    requestId,
                    HttpContext.RequestAborted);

                if (HttpMethods.IsGet(Request.Method) && !download)
                {
                    Response.Headers.CacheControl = "public, max-age=3600";
                }

                if (download)
                {
                    var safeCity = string.Concat(request.CityName.Where(c => !char.IsWhiteSpace(c) && !Path.GetInvalidFileNameChars().Contains(c)));
                    if (string.IsNullOrEmpty(safeCity)) safeCity = "map";
                    return File(imageBytes, "image/png", $"map-{safeCity}-{requestId}.png");
                }

                return File(imageBytes, "image/png");
            }
            catch (CityNotFoundException ex)
            {
                _logger.LogWarning(ex, "City not found: {City} (RequestId: {RequestId})", request.CityName, requestId);
                return NotFound(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid map generation arguments for request {RequestId}", requestId);
                return BadRequest(new { Error = ex.Message });
            }
            catch (ExternalServiceException ex)
            {
                _logger.LogWarning(ex, "External service error for request {RequestId}: {Message}", requestId, ex.Message);
                return StatusCode(StatusCodes.Status502BadGateway, new { Error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Network error communicating with external map services for request {RequestId}", requestId);
                return StatusCode(StatusCodes.Status502BadGateway, new { Error = "Failed to communicate with external map services: " + ex.Message });
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "External service timed out for request {RequestId}", requestId);
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = "A timeout occurred while communicating with external map services. Please try again." });
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation("Map request {RequestId} was cancelled by the client", requestId);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error generating map for request {RequestId}", requestId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred while generating the map. Please check server logs." });
            }
        }

        /// <summary>
        /// Simple GET endpoint for quick testing via query params.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "image/png")]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status502BadGateway, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status504GatewayTimeout, "application/json")]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError, "application/json")]
        public async Task<IActionResult> GetMap(
            [FromQuery, System.ComponentModel.DataAnnotations.Required(ErrorMessage = "City is required.")]
            [System.ComponentModel.DataAnnotations.StringLength(100, MinimumLength = 1, ErrorMessage = "City must be between 1 and 100 characters.")]
            string city, 
            [FromQuery, System.ComponentModel.DataAnnotations.StringLength(1000, ErrorMessage = "Streets parameter must not exceed 1000 characters.")]
            string? streets = null,
            [FromQuery, System.ComponentModel.DataAnnotations.RegularExpression("^(default|light|dark)$", ErrorMessage = "Preset must be 'default', 'light', or 'dark'.")]
            string? preset = "default",
            [FromQuery, System.ComponentModel.DataAnnotations.Range(64, 4096, ErrorMessage = "Width must be between 64 and 4096 pixels.")]
            int width = 800, 
            [FromQuery, System.ComponentModel.DataAnnotations.Range(64, 4096, ErrorMessage = "Height must be between 64 and 4096 pixels.")]
            int height = 600,
            [FromQuery, System.ComponentModel.DataAnnotations.Range(0, 19, ErrorMessage = "Zoom must be between 0 and 19.")]
            int? zoom = null,
            [FromQuery, System.ComponentModel.DataAnnotations.Range(-4096, 4096, ErrorMessage = "OffsetX must be between -4096 and 4096 pixels.")]
            int offsetX = 0,
            [FromQuery, System.ComponentModel.DataAnnotations.Range(-4096, 4096, ErrorMessage = "OffsetY must be between -4096 and 4096 pixels.")]
            int offsetY = 0,
            [FromQuery] bool exactStreetNames = false,
            [FromQuery] bool download = false)
        {
            var streetList = string.IsNullOrWhiteSpace(streets) 
                ? new List<string>() 
                : streets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var request = new MapRequest
            {
                CityName = city,
                HighlightStreets = streetList,
                Width = width,
                Height = height,
                Zoom = zoom,
                OffsetX = offsetX,
                OffsetY = offsetY,
                ExactStreetNames = exactStreetNames,
                Style = new StyleSettings { Preset = preset ?? "default" }
            };
            return await GenerateMap(request, download);
        }
    }
}
