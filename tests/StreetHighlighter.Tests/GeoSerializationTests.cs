using System.Text.Json;
using StreetHighlighter.Services;
using Xunit;

namespace StreetHighlighter.Tests;

public class GeoSerializationTests
{
    [Fact]
    public void GeoPath_SerializesAndDeserializes_PointsCorrectly()
    {
        // Arrange
        var points = new List<GeoPoint>
        {
            new(47.2392, 38.8755),
            new(47.2400, 38.8760)
        };
        var originalPath = new GeoPath(points);

        // Act
        var json = JsonSerializer.Serialize(originalPath);
        var deserialized = JsonSerializer.Deserialize<GeoPath>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Points.Count);
        Assert.Equal(47.2392, deserialized.Points[0].Lat);
        Assert.Equal(38.8755, deserialized.Points[0].Lon);
        Assert.Equal(47.2400, deserialized.Points[1].Lat);
        Assert.Equal(38.8760, deserialized.Points[1].Lon);
    }

    [Fact]
    public void GeoBounds_SerializesAndDeserializes_Correctly()
    {
        // Arrange
        var originalBounds = new GeoBounds(47.1884, 38.7929, 47.2899, 38.9701);

        // Act
        var json = JsonSerializer.Serialize(originalBounds);
        var deserialized = JsonSerializer.Deserialize<GeoBounds>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(47.1884, deserialized.MinLat);
        Assert.Equal(38.7929, deserialized.MinLon);
        Assert.Equal(47.2899, deserialized.MaxLat);
        Assert.Equal(38.9701, deserialized.MaxLon);
    }
}
