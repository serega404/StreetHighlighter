namespace StreetHighlighter.Models;

public sealed record StreetsResponse(
    string CityName,
    IReadOnlyList<string> CityNames,
    IReadOnlyList<string> Streets);
