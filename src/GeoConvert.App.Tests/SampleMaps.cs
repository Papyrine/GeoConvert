namespace GeoConvert.App.Tests;

/// <summary>Small, deterministic in-memory maps used across the snapshot tests.</summary>
static class SampleMaps
{
    // Two overlapping squares, a triangle and a line — enough geometry to exercise the renderer, the
    // diff overlay and the structural summary without depending on any external data file.
    public static FeatureCollection A() =>
    [
        new Feature(
            new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]]),
            Props(("name", "Square"), ("pop", 100L))),
        new Feature(
            new Polygon([[new(12, 0), new(20, 0), new(16, 8), new(12, 0)]]),
            Props(("name", "Tri"))),
        new Feature(
            new LineString([new(0, 12), new(20, 12)]),
            Props(("name", "Road"))),
    ];

    public static FeatureCollection B() =>
    [
        new Feature(
            new Polygon([[new(1, 1), new(11, 1), new(11, 11), new(1, 11), new(1, 1)]]),
            Props(("name", "Square"), ("pop", 100L), ("iso", "SQ"))),
        new Feature(
            new Point(new(16, 4)),
            Props(("name", "Dot"), ("iso", "DT"))),
    ];

    static IDictionary<string, object?> Props(params (string Key, object? Value)[] pairs)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
        {
            properties[key] = value;
        }

        return properties;
    }
}
