using System.ComponentModel.DataAnnotations;
using StreetHighlighter.Models;
using Xunit;

namespace StreetHighlighter.Tests;

public class ValidationAndModelTests
{
    [Fact]
    public void MapRequest_ValidRequest_PassesValidation()
    {
        var request = new MapRequest
        {
            CityName = "Таганрог",
            HighlightStreets = new List<string> { "Петровская", "Чехова" },
            Width = 1200,
            Height = 800,
            Zoom = 14,
            OffsetX = 250,
            OffsetY = -100,
            Style = new StyleSettings
            {
                Preset = "dark",
                HighlightColor = "#FF5733",
                Opacity = 0.8f,
                StrokeWidth = 4.5f
            }
        };

        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData(-4097, 0, nameof(MapRequest.OffsetX))]
    [InlineData(4097, 0, nameof(MapRequest.OffsetX))]
    [InlineData(0, -4097, nameof(MapRequest.OffsetY))]
    [InlineData(0, 4097, nameof(MapRequest.OffsetY))]
    public void MapRequest_InvalidOffset_FailsValidation(int offsetX, int offsetY, string memberName)
    {
        var request = new MapRequest
        {
            CityName = "Москва",
            OffsetX = offsetX,
            OffsetY = offsetY
        };

        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(memberName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(4097)]
    public void MapRequest_InvalidDimensions_FailsValidation(int invalidDimension)
    {
        var request = new MapRequest
        {
            CityName = "Москва",
            Width = invalidDimension,
            Height = 800
        };

        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(MapRequest.Width)));
    }

    [Theory]
    [InlineData("#FF5733")]
    [InlineData("#F53")]
    [InlineData("#F538")]
    [InlineData("#FF573380")]
    public void StyleSettings_ValidHexColors_PassValidation(string hex)
    {
        var style = new StyleSettings
        {
            HighlightColor = hex
        };

        var context = new ValidationContext(style);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(style, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("FF5733")]
    public void StyleSettings_InvalidHexColors_FailValidation(string invalidHex)
    {
        var style = new StyleSettings
        {
            HighlightColor = invalidHex
        };

        var context = new ValidationContext(style);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(style, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(StyleSettings.HighlightColor)));
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void StyleSettings_InvalidOpacity_FailsValidation(float invalidOpacity)
    {
        var style = new StyleSettings
        {
            Opacity = invalidOpacity
        };

        var context = new ValidationContext(style);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(style, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(StyleSettings.Opacity)));
    }

    [Fact]
    public void MapRequest_StreetNameExceeding200Chars_FailsValidation()
    {
        var request = new MapRequest
        {
            CityName = "Москва",
            HighlightStreets = new List<string> { new string('a', 201) }
        };

        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.ErrorMessage != null && r.ErrorMessage.Contains("200 characters"));
    }
}
