using System.ComponentModel.DataAnnotations;

namespace StreetHighlighter.Models
{
    public class StyleSettings
    {
        /// <summary>
        /// Visual preset: 'default', 'dark', 'light'
        /// </summary>
        [RegularExpression("^(default|light|dark)$", ErrorMessage = "Preset must be 'default', 'light', or 'dark'.")]
        public string Preset { get; set; } = "default";

        /// <summary>
        /// Hex color code for highlighted streets (e.g., "#FF0000")
        /// </summary>
        [RegularExpression("^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", ErrorMessage = "HighlightColor must be a valid hex color (e.g., '#FF5733').")]
        public string HighlightColor { get; set; } = "#FF5733";

        /// <summary>
        /// Opacity of highlighted streets (0.0 to 1.0)
        /// </summary>
        [Range(0.0, 1.0, ErrorMessage = "Opacity must be between 0.0 and 1.0.")]
        public float Opacity { get; set; } = 1.0f;

        /// <summary>
        /// Stroke width of highlighted streets in pixels
        /// </summary>
        [Range(0.1, 50.0, ErrorMessage = "StrokeWidth must be between 0.1 and 50.0 pixels.")]
        public float StrokeWidth { get; set; } = 5.0f;
    }
}
