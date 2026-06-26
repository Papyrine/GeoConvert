namespace GeoConvert.App;

/// <summary>
/// Desktop-facing metadata for a <see cref="GeoFormat"/>: how to name it, which file extensions it
/// owns, and whether it can be read / written. The desktop has a real filesystem, so (unlike the
/// browser sample) the path-based Shapefile is a first-class format here.
/// </summary>
public record FormatInfo(
    GeoFormat Format,
    string DisplayName,
    string Extension,
    IReadOnlyList<string> Extensions,
    bool CanRead,
    bool CanWrite)
{
    /// <summary>A single-format file-dialog filter clause, e.g. <c>GeoJSON (*.geojson;*.json)|*.geojson;*.json</c>.</summary>
    public string DialogFilter
    {
        get
        {
            var patterns = string.Join(';', Extensions.Select(_ => $"*{_}"));
            return $"{DisplayName} ({patterns})|{patterns}";
        }
    }
}
