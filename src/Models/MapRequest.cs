using System.ComponentModel.DataAnnotations;

namespace StreetHighlighter.Models
{
    public class MapRequest : IValidatableObject
    {
        public const int MaxStreetLength = 200;

        [Required(ErrorMessage = "CityName is required.")]
        [StringLength(100, MinimumLength = 1, ErrorMessage = "CityName must be between 1 and 100 characters.")]
        public string CityName { get; set; } = string.Empty;

        [MaxLength(100, ErrorMessage = "A maximum of 100 streets can be highlighted per request.")]
        public List<string> HighlightStreets { get; set; } = new();

        public bool ExactStreetNames { get; set; }

        public StyleSettings Style { get; set; } = new();
        
        // Output image size
        [Range(64, 4096, ErrorMessage = "Width must be between 64 and 4096 pixels.")]
        public int Width { get; set; } = 800;

        [Range(64, 4096, ErrorMessage = "Height must be between 64 and 4096 pixels.")]
        public int Height { get; set; } = 600;

        // Forced Zoom level (optional). 
        // If null, the map will auto-fit the city bounds.
        [Range(0, 19, ErrorMessage = "Zoom must be between 0 and 19.")]
        public int? Zoom { get; set; }

        // Shift the rendered map content in output pixels.
        [Range(-4096, 4096, ErrorMessage = "OffsetX must be between -4096 and 4096 pixels.")]
        public int OffsetX { get; set; }

        [Range(-4096, 4096, ErrorMessage = "OffsetY must be between -4096 and 4096 pixels.")]
        public int OffsetY { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (HighlightStreets != null)
            {
                for (var i = 0; i < HighlightStreets.Count; i++)
                {
                    var street = HighlightStreets[i];
                    if (street != null && street.Length > MaxStreetLength)
                    {
                        yield return new ValidationResult(
                            $"Street name at index {i} must not exceed {MaxStreetLength} characters.",
                            new[] { $"{nameof(HighlightStreets)}[{i}]" });
                    }
                }
            }
        }
    }
}
