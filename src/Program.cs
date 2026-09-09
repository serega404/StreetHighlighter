using StreetHighlighter.Configuration;
using StreetHighlighter.Infrastructure;
using StreetHighlighter.Services;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger configuration
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "Optional API key. Set the value of the X-API-Key header when API_KEY is configured on the server.",
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document, null)] = []
    });
});

// Health checks and CORS
builder.Services.AddHealthChecks()
    .AddCheck("cache_directory", () =>
    {
        try
        {
            var cachePath = Path.Combine(builder.Environment.ContentRootPath, "Cache");
            Directory.CreateDirectory(cachePath);
            var testFile = Path.Combine(cachePath, $".health_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "health");
            File.Delete(testFile);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Cache directory is writable.");
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Cache directory is not writable.", ex);
        }
    });
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddOptions<ExternalServicesOptions>()
    .Bind(builder.Configuration.GetSection(ExternalServicesOptions.SectionName))
    .Validate(ExternalServicesOptions.IsValid, "External service URLs or retry settings are invalid.")
    .ValidateOnStart();

// Application Services
foreach (var clientName in new[]
         {
             ExternalHttpClientNames.Nominatim,
             ExternalHttpClientNames.Overpass,
             ExternalHttpClientNames.Tiles
         })
{
    builder.Services.AddHttpClient(clientName, (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<ExternalServicesOptions>>().Value;
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
    });
}

builder.Services.AddSingleton<IExternalHttpService, ExternalHttpService>();
builder.Services.AddScoped<IGeoDataService, GeoDataService>();
builder.Services.AddSingleton<ITileCacheService, TileCacheService>();
builder.Services.AddScoped<IMapRendererService, MapRendererService>();
builder.Services.AddTransient<PerformanceLogger>();
builder.Services.AddHostedService<CacheCleanupHostedService>();

var app = builder.Build();

app.UseCors();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("EnableSwagger"))
{
    app.UseSwagger(options =>
    {
        options.RouteTemplate = "api/docs/{documentName}/swagger.json";
    });
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "api/docs";
        options.SwaggerEndpoint("v1/swagger.json", "StreetHighlighter API v1");
    });
}

app.MapControllers();

app.Run();
