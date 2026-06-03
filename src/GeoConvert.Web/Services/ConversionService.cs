namespace GeoConvert.Web.Services;

/// <summary>
/// Wraps <see cref="GeoConverter"/> for the browser: everything flows through in-memory byte arrays
/// because a WASM client has no filesystem. Shapefile is excluded — it spans multiple sibling files
/// and so is path-based, which the stream APIs here can't express.
/// </summary>
public static class ConversionService
{
    static IReadOnlyList<FormatInfo> AllFormats { get; } =
    [
        new(GeoFormat.GeoJson, "GeoJSON", ".geojson", "application/geo+json", CanRead: true, CanWrite: true),
        new(GeoFormat.TopoJson, "TopoJSON", ".topojson", "application/json", CanRead: true, CanWrite: true),
        new(GeoFormat.FlatGeobuf, "FlatGeobuf", ".fgb", "application/octet-stream", CanRead: true, CanWrite: true),
        new(GeoFormat.Kml, "KML", ".kml", "application/vnd.google-earth.kml+xml", CanRead: true, CanWrite: true),
        new(GeoFormat.Kmz, "KMZ", ".kmz", "application/vnd.google-earth.kmz", CanRead: true, CanWrite: true),
        new(GeoFormat.Gpx, "GPX", ".gpx", "application/gpx+xml", CanRead: true, CanWrite: true),
        new(GeoFormat.Wkt, "WKT", ".wkt", "text/plain", CanRead: true, CanWrite: true),
        new(GeoFormat.Wkb, "WKB", ".wkb", "application/octet-stream", CanRead: true, CanWrite: true),
        new(GeoFormat.Csv, "CSV", ".csv", "text/csv", CanRead: true, CanWrite: true),
        new(GeoFormat.GeoParquet, "GeoParquet", ".parquet", "application/octet-stream", CanRead: true, CanWrite: true),
        new(GeoFormat.Png, "PNG image", ".png", "image/png", CanRead: false, CanWrite: true),
    ];

    /// <summary>Formats that can be read from a single uploaded stream (excludes path-only Shapefile and write-only PNG).</summary>
    public static IReadOnlyList<FormatInfo> ReadableFormats { get; } =
        [.. AllFormats.Where(_ => _.CanRead)];

    /// <summary>Formats that can be written to a single downloadable stream (excludes path-only Shapefile).</summary>
    public static IReadOnlyList<FormatInfo> WritableFormats { get; } =
        [.. AllFormats.Where(_ => _.CanWrite)];

    /// <summary>
    /// Value for a file input's <c>accept</c> attribute: every readable format's canonical extension plus
    /// the aliases <see cref="GeoConverter"/> also detects (<c>.json</c> → GeoJSON, <c>.geoparquet</c> →
    /// GeoParquet), so a file with one of those isn't filtered out. It's only a picker hint — a user can
    /// still choose "all files", so <see cref="DetectReadable"/> validates the actual selection.
    /// </summary>
    public static string ReadableAccept { get; } =
        string.Join(',', ReadableFormats.Select(_ => _.Extension).Concat([".json", ".geoparquet"]));

    /// <summary>Looks up the <see cref="FormatInfo"/> for a format, or null when it isn't browser-supported.</summary>
    public static FormatInfo? Find(GeoFormat format) =>
        AllFormats.FirstOrDefault(_ => _.Format == format);

    /// <summary>
    /// Infers the format from an uploaded file name. Returns null when the extension is unknown or maps
    /// to a format this app can't read in the browser (e.g. a Shapefile).
    /// </summary>
    public static FormatInfo? DetectReadable(string fileName)
    {
        if (!GeoConverter.TryDetectFormat(fileName, out var format))
        {
            return null;
        }

        return ReadableFormats.FirstOrDefault(_ => _.Format == format);
    }

    public static FeatureCollection Read(byte[] input, GeoFormat format, IProgress<ConvertProgress>? progress = null)
    {
        using var stream = new MemoryStream(input);
        return GeoConverter.Read(stream, format, progress);
    }

    public static byte[] Write(FeatureCollection features, GeoFormat format, IProgress<ConvertProgress>? progress = null)
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(features, stream, format, progress);
        return stream.ToArray();
    }

    /// <summary>
    /// Renders a PNG with caller-chosen layout. <paramref name="projection"/> selects the map
    /// projection; <paramref name="maxDimension"/>, when positive, caps the image's longer edge at
    /// that many pixels (the shorter edge follows the aspect ratio) — otherwise the renderer's default
    /// size applies. <paramref name="progress"/> is reported per feature rasterised.
    /// </summary>
    public static byte[] RenderPng(FeatureCollection features, MapProjection projection, int maxDimension, IProgress<ConvertProgress>? progress = null)
    {
        var options = new RenderOptions
        {
            Projection = projection,
            StrokeAutoScale = true,
            // Paint the projection's world envelope as ocean under every feature. For Goode this is
            // what makes the lobed shape — the curving lobe-boundary meridians and the equator/pole
            // arcs — visible; without it Goode renders as floating continents in a void with none of
            // the projection's defining outlines. For the rectangular projections the envelope is
            // the whole canvas, so this just doubles as a sea-colour background for the world map.
            Ocean = new(200, 220, 240),
            // Render-time cartographic selection: drop polygons / lines whose projected bbox is
            // below 1 px in both axes. Without this, world-scale renders of detailed admin datasets
            // (the sample world map's thousands of Indonesian / Norwegian / Arctic-Archipelago
            // islands) paint every sub-pixel feature as a 1-px stroke speck, turning dense coasts
            // into visual noise instead of recognisable land. At 1 the threshold matches the
            // "if you can't paint it cleanly, don't paint it at all" floor; raising it (2 or 4)
            // prunes more aggressively for thumbnails. Zooming in (a tighter Bounds or larger
            // canvas) naturally surfaces the pruned features again — the filter is render-scale
            // adaptive, not destructive.
            MinFeaturePixels = 1,
            Progress = progress
        };
        if (maxDimension > 0)
        {
            options.MaxDimension = maxDimension;
        }

        return MapRenderer.RenderPng(features, options);
    }

    public static byte[] Convert(byte[] input, GeoFormat from, GeoFormat to, IProgress<ConvertProgress>? progress = null) =>
        Write(Read(input, from, progress), to, progress);
}
