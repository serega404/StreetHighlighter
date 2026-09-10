# StreetHighlighter

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/serega404/streethighlighter.svg)](https://github.com/serega404/streethighlighter/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
![GitHub last commit](https://img.shields.io/github/last-commit/serega404/StreetHighlighter)
![GitHub repo size](https://img.shields.io/github/repo-size/serega404/StreetHighlighter)

[Русская версия](README.md)

> **Disclaimer:** The initial version of this project was written manually, after which development was paused for some time. It was later completed primarily with the help of vibe coding. A substantial portion of the resulting code has not undergone detailed human review. However, the project went through numerous analysis iterations using various AI models, which were repeatedly asked to identify issues, suggest improvements, and assess the quality of the implementation decisions.

> An ASP.NET Core HTTP API that generates PNG city maps with selected streets highlighted over OpenStreetMap raster tiles.

StreetHighlighter accepts a city name, a list of streets, and rendering options. It retrieves city bounds and road geometry from OpenStreetMap services, downloads the required tiles, and composes the final image with SkiaSharp. The project can serve as a foundation for route posters, visited-street maps, and other server-side map image generators.

Example result:

![Example of highlighted streets on a map](result_example.png)

## Features

- PNG generation through either `GET` or `POST`;
- automatic zoom-to-fit based on city bounds;
- an explicit `zoom` level when predictable coverage is required;
- highlighting of multiple streets with one style;
- configurable line color, opacity, and width;
- `default`, `light`, and `dark` presets;
- disk caching for city bounds, street geometry, and tiles;
- Swagger UI and an OpenAPI document;
- timing logs for geodata retrieval and rendering stages.

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0);
- internet access to Nominatim, Overpass API, and the OpenStreetMap tile server;
- write access to the `src/Cache` directory;
- an operating system supported by SkiaSharp.

## Quick start

Restore dependencies and start the HTTP profile from the repository root:

```bash
dotnet restore streethighlighter.sln
dotnet run --project src/StreetHighlighter.csproj --launch-profile http
```

Open Swagger UI at:

```text
http://localhost:5171/api/docs
```

## API usage

Both generation methods use `/api/v1/map`. The simplified `GET` request is convenient for manual checks, while `POST` exposes all rendering options. A separate `GET /api/v1/streets` endpoint returns street names from OSM.

### API key

The API is open by default. To protect the API, set a non-empty `API_KEY` environment variable on the server:

```bash
API_KEY='replace-with-a-long-random-secret' dotnet run --project src/StreetHighlighter.csproj --launch-profile http
```

When `API_KEY` is configured, both `/api/v1/map` methods and `GET /api/v1/streets` require the same value in the `X-API-Key` header; missing or invalid keys return `401 Unauthorized`. `/health` and Swagger UI remain available without a key. In Swagger, select **Authorize** and enter the key before sending a request.

### GET `/api/v1/map`

Query string parameters:

| Parameter          | Type     | Default   | Description                                                                                                                        |
| ------------------ | -------- | --------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `city`             | `string` | —         | Required city name.                                                                                                                |
| `streets`          | `string` | empty     | Comma-separated street names. Surrounding whitespace is trimmed.                                                                   |
| `preset`           | `string` | `default` | Visual preset: `default`, `light`, or `dark`.                                                                                      |
| `width`            | `int`    | `800`     | PNG width in pixels.                                                                                                               |
| `height`           | `int`    | `600`     | PNG height in pixels.                                                                                                              |
| `zoom`             | `int?`   | auto      | Explicit Web Mercator zoom level. Without it, city bounds are fitted automatically.                                                |
| `offsetX`          | `int`    | `0`       | Horizontal map-content offset in pixels. Positive values move the map right.                                                       |
| `offsetY`          | `int`    | `0`       | Vertical map-content offset in pixels. Positive values move the map down.                                                          |
| `exactStreetNames` | `bool`   | `false`   | When `true`, the full street name must match the OSM `name` value; matching remains case-insensitive.                              |
| `download`         | `bool`   | `false`   | When `true`, returns `Content-Disposition: attachment` to download file. Default is `false` (inline for `<img>` tags and preview). |

Example:

```bash
curl --get 'http://localhost:5171/api/v1/map' \
  --header 'X-API-Key: replace-with-your-key' \
  --data-urlencode 'city=Berlin' \
  --data-urlencode 'streets=Unter den Linden,Friedrichstraße' \
  --data-urlencode 'preset=dark' \
  --data-urlencode 'width=1200' \
  --data-urlencode 'height=800' \
  --data-urlencode 'offsetX=100' \
  --data-urlencode 'offsetY=-50' \
  --data-urlencode 'exactStreetNames=true' \
  --output map.png
```

The `GET` endpoint does not expose highlight color, opacity, or line width. Use `POST` for these options.

### GET `/api/v1/streets`

Returns unique OSM `name` values for named roads within the resolved city's bounding box. Names are sorted case-insensitively. Objects without both `highway` and `name` tags are excluded.

```bash
curl --get 'http://localhost:5171/api/v1/streets' \
  --header 'X-API-Key: replace-with-your-key' \
  --data-urlencode 'city=New York'
```

Example response:

```json
{
  "cityName": "New York",
  "cityNames": [
    "New York",
    "New York, United States"
  ],
  "streets": [
    "100th Avenue",
    "100th Drive",
    "100th Place",
    "100th Road",
    "100th Street",
    ...
  ]
}
```

`cityName` contains the short resolved name, or the original query when Nominatim does not provide one. `cityNames` contains the available unique variants in this order: the client query, short name, and full Nominatim display name. The list reflects current OSM data: streets without their own `name` tag, including names found only in `addr:street`, are not included.

The endpoint returns `400` for an invalid `city` parameter, `404` when the city is not found, `502`/`504` for external-service failures, and `500` for an unexpected internal error.

### POST `/api/v1/map`

Full request example:

```bash
curl --request POST 'http://localhost:5171/api/v1/map' \
  --header 'Content-Type: application/json' \
  --header 'X-API-Key: replace-with-your-key' \
  --data '{
    "cityName": "New York",
    "highlightStreets": [
      "100th Avenue",
      "5th Avenue"
    ],
    "style": {
      "preset": "dark",
      "highlightColor": "#00E5FF",
      "opacity": 0.9,
      "strokeWidth": 6
    },
    "width": 1200,
    "height": 800,
    "zoom": null,
    "offsetX": 100,
    "offsetY": -50,
    "exactStreetNames": true
  }' \
  --output map.png
```

Request body fields:

| Field              | Type       | Default       | Description                                                                 |
| ------------------ | ---------- | ------------- | --------------------------------------------------------------------------- |
| `cityName`         | `string`   | —             | Required city name.                                                         |
| `highlightStreets` | `string[]` | `[]`          | Streets to highlight.                                                       |
| `style`            | `object`   | default style | Base-map and line options. Do not pass `null`.                              |
| `width`            | `int`      | `800`         | Output width in pixels.                                                     |
| `height`           | `int`      | `600`         | Output height in pixels.                                                    |
| `zoom`             | `int?`     | `null`        | Explicit zoom level, or automatic city fitting when omitted.                |
| `offsetX`          | `int`      | `0`           | Horizontal map offset in pixels: positive moves right, negative moves left. |
| `offsetY`          | `int`      | `0`           | Vertical map offset in pixels: positive moves down, negative moves up.      |
| `exactStreetNames` | `bool`     | `false`       | Require a full case-insensitive street-name match.                          |

The `style` object accepts:

| Field            | Type     | Default   | Description                            |
| ---------------- | -------- | --------- | -------------------------------------- |
| `preset`         | `string` | `default` | `default`, `light`, or `dark`.         |
| `highlightColor` | `string` | `#FF5733` | Line color in HEX notation.            |
| `opacity`        | `float`  | `1.0`     | Highlight opacity from `0.0` to `1.0`. |
| `strokeWidth`    | `float`  | `5.0`     | Line width in pixels.                  |

`default` uses standard light tiles, `light` adds a soft white blend, and `dark` inverts luminance for a dark theme style.

### Response

On successful generation, the server returns:

- `200 OK`;
- `Content-Type: image/png`;
- caching header `Cache-Control: public, max-age=3600` (for `GET` requests without the `download` flag);
- when `download=true` is requested — `Content-Disposition: attachment; filename="map-{city}-{requestId}.png"`. By default, the image is served inline.

An empty street list still produces a city map, but without vector highlights.

### Errors

| Status | Cause |
| --- | --- |
| `400 Bad Request` | The required `cityName` field is missing, a street name exceeds 200 characters, >100 streets requested, or model binding fails. |
| `404 Not Found` | The city is not found by the geocoding service. |
| `502 Bad Gateway` | An external mapping service error occurred (Nominatim, Overpass API, or tile provider). |
| `504 Gateway Timeout` | An external mapping service request timed out. |
| `500 Internal Server Error` | An unexpected internal application error occurred. |

## Street matching behavior

- Overpass matching is case-insensitive.
- By default, names are matched as substrings to preserve existing behavior. With `exactStreetNames=true`, the complete `name` tag must match; for example, `1st New Lane` does not match `11th New Lane`.
- Street names are escaped and combined into an optimized regular expression for Overpass QL, significantly reducing Overpass query complexity and latency.
- Search is restricted to the city's bounding box resolved via Nominatim.
- Nominatim selects the best matching `place` or `boundary` result. Qualify ambiguous names with a region and country, for example `Springfield, Illinois, USA`.
- Non-existent city lookups are negatively cached for 24 hours to prevent repeated query latency and Nominatim quota exhaustion.
- Nominatim requests are serialized with a minimum 1.1-second interval between requests, while Overpass allows at most two concurrent requests. Retries are used for transient failures, including HTTP `429` responses.

## Caching

The cache is created relative to the project content root:

```text
src/Cache/
├── Geo/
│   ├── bounds_<city>_<hash>.json
│   ├── bounds_notfound_<hash>.json
│   ├── city_names_<hash>.json
│   ├── street_names_<hash>.json
│   └── streets_<hash>.json
└── Tiles/
    └── <provider-hash>/<z>/<x>/<y>.png
```

- `bounds_*.json` contains resolved city bounds;
- `bounds_notfound_*.json` contains negative cache markers for unresolved cities (TTL 24 hours);
- `city_names_*.json` contains short and full city names returned by Nominatim;
- `street_names_*.json` contains the sorted unique street-name list;
- `streets_*.json` contains geometry for a city and normalized street list;
- `Tiles` stores tiles by their `z/x/y` coordinates;
- final PNG files are not cached and are rendered for every request;
- the background `CacheCleanupHostedService` runs every 6 hours:
  - removes temporary files (`.tmp_*`) older than 1 hour;
  - removes cache files unaccessed for more than 14 days;
  - if total cache size exceeds 1 GB, removes the least recently used files until the cache is reduced to 800 MB;
  - cleans up empty nested directories while preserving system root folders `Cache/Geo` and `Cache/Tiles`.

To force a refresh, stop the application and remove the relevant files under `src/Cache/Geo` or tiles under `src/Cache/Tiles`. Do not clear the cache while a request is writing to it.

## Configuration

### External services

External service settings (Nominatim, Overpass API, tile server), timeouts, retry counts, delays, maximum response sizes (`MaxResponseBytes`), and the `User-Agent` are configured in the `ExternalServices` section of `appsettings.json` (the `ExternalServicesOptions` class). Settings are validated on application startup (`ValidateOnStart`).

### Docker Compose

Provide `API_KEY` (if required) and a real contact in `ExternalServices__UserAgent` when using public OSM services:

```bash
API_KEY='replace-with-a-long-random-secret' \
ExternalServices__UserAgent='StreetHighlighter/1.0 (ops@example.com)' \
docker compose up --build -d
```

Swagger stays disabled unless `EnableSwagger=true` is explicitly provided. The cache persists in the `street-highlighter-cache` named volume without host permission setup.

City bounds and names are refreshed after 7 days; street-name lists and street geometry are refreshed after 24 hours. Tile caches are namespaced by a hash of the provider URL, so changing `ExternalServices:Tiles:Url` cannot mix old tiles with the new source.

Current default integrations:

| Purpose                  | Service                                          |
| ------------------------ | ------------------------------------------------ |
| Resolve city bounds      | `https://nominatim.openstreetmap.org/search`     |
| Retrieve street geometry | `https://overpass-api.de/api/interpreter`        |
| Fetch raster tiles       | `https://tile.openstreetmap.org/{z}/{x}/{y}.png` |

## Project structure

```text
StreetHighlighter/
├── streethighlighter.sln
├── Dockerfile
├── src/
│   ├── Configuration/
│   │   └── ExternalServicesOptions.cs
│   ├── Controllers/
│   │   ├── MapController.cs
│   │   └── StreetsController.cs
│   ├── Infrastructure/
│   │   └── PerformanceLogger.cs
│   ├── Models/
│   │   ├── MapRequest.cs
│   │   ├── StreetsResponse.cs
│   │   └── StyleSettings.cs
│   ├── Services/
│   │   ├── CacheCleanupHostedService.cs
│   │   ├── ExternalHttpService.cs
│   │   ├── GeoDataService.cs
│   │   ├── MapRendererService.cs
│   │   └── TileCacheService.cs
│   ├── Properties/
│   │   └── launchSettings.json
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── StreetHighlighter.csproj
└── tests/
    └── StreetHighlighter.Tests/
```

Primary dependencies:

- ASP.NET Core for the HTTP API, dependency injection, and configuration;
- Swashbuckle for Swagger UI and OpenAPI;
- BruTile for the Web Mercator schema and tile calculations;
- SkiaSharp for raster rendering and PNG encoding;
- `HttpClient` for requests to OSM services.

## Build and development

Debug build:

```bash
dotnet build streethighlighter.sln
```

Release build:

```bash
dotnet build streethighlighter.sln --configuration Release
```

Publish into a separate directory:

```bash
dotnet publish src/StreetHighlighter.csproj \
  --configuration Release \
  --output artifacts/publish
```

Run automated tests:

```bash
dotnet test streethighlighter.sln
```

The test suite covers model validation, controller endpoints, cache cleanup preservation, tile data integrity, and negative geocoding cache.

## Troubleshooting

### City not found

Check spelling and qualify the city with its region or country. Make sure `nominatim.openstreetmap.org` is reachable from the application host. If the city was previously not found, remove the corresponding `bounds_notfound_*.json` file under `Cache/Geo` to reset the negative cache.

### The map is generated, but no streets are highlighted

Check object names in OpenStreetMap: matching roads must have a `name` tag. Also inspect the logs for Overpass errors or overload responses.

### Parts of the map are blank

Look for `Failed to load tile` messages. Common causes include a network failure, public tile-server limits, or a corrupt file under `Cache/Tiles`. The service automatically evicts corrupted tile files upon decoding failure.

### Non-default style or image settings fail

Request parameters are validated: `width` and `height` (64 to 4096), `zoom` (0 to 19), `offsetX` and `offsetY` (-4096 to 4096 pixels), `opacity` (0.0 to 1.0), `strokeWidth` (0.1 to 50.0), `highlightColor` (HEX format `#RGB`, `#RRGGBB`, or `#RRGGBBAA`), streets are limited to 100 per request, and each street name cannot exceed 200 characters. Invalid values return `400 Bad Request`.

## Production readiness

The current version is a foundational API and needs additional hardening before public deployment:

- configure rate limiting per IP;
- when running in Docker and mounting a host directory to `/app/Cache`, ensure the host directory is owned by the container user (UID 1654):

```bash
chown -R 1654:1654 /path/to/host/Cache
```

- do not treat public OSM services as providers with an SLA (deploy dedicated local Nominatim, Overpass, and tile instances for production workloads).

Public OpenStreetMap infrastructure has its own limits. Read the [Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/), [Nominatim Usage Policy](https://operations.osmfoundation.org/policies/nominatim/), and [Overpass API documentation](https://wiki.openstreetmap.org/wiki/Overpass_API). Replace the demonstration `User-Agent` and `contact@StreetHighlighter.local` address with real application details before public deployment.

Generated images automatically include a compact “© OpenStreetMap contributors” label in the bottom-right corner. The label uses the bundled open-source Noto Sans font under the SIL Open Font License 1.1. When using another tile provider, check its attribution and licensing requirements separately.

## Known limitations

- PNG is the only output format;
- only the most relevant Nominatim result is selected;
- the base tile style is fixed (OSM Standard);
- `dark` applies a grayscale inversion instead of using a dedicated dark tile style;
- final rendered PNG images are not cached on disk;

## License

The project source code is distributed under the [MIT License](LICENSE)

The project license does not automatically cover OpenStreetMap data, tiles, or external services. When using them, follow the requirements of the [OpenStreetMap Foundation](https://www.osmfoundation.org/) and the applicable providers separately.

The bundled `src/Assets/Fonts/NotoSans-Regular.ttf` font is distributed under the SIL Open Font License 1.1; its license text is stored alongside it in `NotoSans-OFL.txt`.
