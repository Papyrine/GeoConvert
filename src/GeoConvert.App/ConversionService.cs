namespace GeoConvert.App;

/// <summary>
/// Wraps <see cref="GeoConverter"/> and the renderers for the desktop app — the counterpart of the
/// Blazor app's conversion service, but path/stream based and filesystem-aware, so the path-only
/// Shapefile is a first-class format here (the browser version had to exclude it). Everything the GUI
/// and the CLI do — detect, read, simplify, write, render, choose a PNG backend — funnels through here.
/// </summary>
public static class ConversionService
{
    static IReadOnlyList<FormatInfo> AllFormats { get; } =
    [
        new(GeoFormat.GeoJson, "GeoJSON", ".geojson", [".geojson", ".json"], CanRead: true, CanWrite: true),
        new(GeoFormat.TopoJson, "TopoJSON", ".topojson", [".topojson"], CanRead: true, CanWrite: true),
        new(GeoFormat.Shapefile, "Shapefile", ".shp", [".shp"], CanRead: true, CanWrite: true),
        new(GeoFormat.FlatGeobuf, "FlatGeobuf", ".fgb", [".fgb"], CanRead: true, CanWrite: true),
        new(GeoFormat.Kml, "KML", ".kml", [".kml"], CanRead: true, CanWrite: true),
        new(GeoFormat.Kmz, "KMZ", ".kmz", [".kmz"], CanRead: true, CanWrite: true),
        new(GeoFormat.Gpx, "GPX", ".gpx", [".gpx"], CanRead: true, CanWrite: true),
        new(GeoFormat.Wkt, "WKT", ".wkt", [".wkt"], CanRead: true, CanWrite: true),
        new(GeoFormat.Wkb, "WKB", ".wkb", [".wkb"], CanRead: true, CanWrite: true),
        new(GeoFormat.Csv, "CSV", ".csv", [".csv"], CanRead: true, CanWrite: true),
        new(GeoFormat.GeoParquet, "GeoParquet", ".parquet", [".parquet", ".geoparquet"], CanRead: true, CanWrite: true),
        new(GeoFormat.Png, "PNG image", ".png", [".png"], CanRead: false, CanWrite: true),
        new(GeoFormat.Svg, "SVG image", ".svg", [".svg"], CanRead: false, CanWrite: true),
    ];

    public static IReadOnlyList<FormatInfo> Formats => AllFormats;

    /// <summary>Formats that can be read into features (everything except the write-only images).</summary>
    public static IReadOnlyList<FormatInfo> ReadableFormats { get; } = [.. AllFormats.Where(_ => _.CanRead)];

    /// <summary>Formats that can be written (every format, including the render-only PNG/SVG).</summary>
    public static IReadOnlyList<FormatInfo> WritableFormats { get; } = [.. AllFormats.Where(_ => _.CanWrite)];

    /// <summary>Every distinct extension across the readable formats — used for the open dialog and file associations.</summary>
    public static IReadOnlyList<string> ReadableExtensions { get; } =
        [.. ReadableFormats.SelectMany(_ => _.Extensions).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The write-only image formats whose write is a render (projection + size apply), not a plain codec write.</summary>
    public static bool IsRendered(GeoFormat format) =>
        format is GeoFormat.Png or GeoFormat.Svg;

    public static FormatInfo? Find(GeoFormat format) =>
        AllFormats.FirstOrDefault(_ => _.Format == format);

    /// <summary>Infers the format of a file name, or null when the extension is unknown.</summary>
    public static FormatInfo? Detect(string fileName) =>
        GeoConverter.TryDetectFormat(fileName, out var format) ? Find(format) : null;

    public static FeatureCollection Read(string path, GeoFormat format, IProgress<ConvertProgress>? progress = null) =>
        // GeoConverter.Read is path-based and already special-cases Shapefile's sibling .shp/.shx/.dbf.
        GeoConverter.Read(path, format, progress);

    /// <summary>
    /// Writes <paramref name="features"/> to <paramref name="path"/> in <paramref name="format"/>, applying
    /// the relevant options: a render (honouring the chosen <see cref="RenderSettings.Renderer"/>) for
    /// PNG/SVG, the option-carrying overloads for KMZ/GeoParquet, and the plain codec write (Shapefile
    /// included) otherwise.
    /// </summary>
    public static void Save(
        FeatureCollection features,
        string path,
        GeoFormat format,
        RenderSettings render,
        KmzSettings kmz,
        GeoParquetSettings parquet,
        IProgress<ConvertProgress>? progress = null)
    {
        switch (format)
        {
            case GeoFormat.Png:
                File.WriteAllBytes(path, RenderPng(features, render, progress));
                break;
            case GeoFormat.Svg:
                MapRenderer.RenderSvg(features, path, RenderOptionsFor(render, progress));
                break;
            case GeoFormat.Kmz:
                WriteKmz(features, path, kmz);
                break;
            case GeoFormat.GeoParquet:
                WriteGeoParquet(features, path, parquet);
                break;
            default:
                GeoConverter.Write(features, path, format, progress);
                break;
        }
    }

    static void WriteKmz(FeatureCollection features, string path, KmzSettings settings)
    {
        using var stream = File.Create(path);
        Kmz.Write(stream, features, settings.Compression);
    }

    static void WriteGeoParquet(FeatureCollection features, string path, GeoParquetSettings settings)
    {
        using var stream = File.Create(path);
        GeoParquet.Write(stream, features, settings.Codec, settings.GzipLevel);
    }

    /// <summary>
    /// Renders a PNG through the chosen <see cref="RenderSettings.Renderer"/> backend (built-in software
    /// rasterizer or ImageSharp).
    /// </summary>
    public static byte[] RenderPng(FeatureCollection features, RenderSettings render, IProgress<ConvertProgress>? progress = null)
    {
        var options = RenderOptionsFor(render, progress);
        return render.Renderer switch
        {
            RendererBackend.ImageSharp => ImageSharpRenderer.RenderPng(features, options),
            _ => MapRenderer.RenderPng(features, options),
        };
    }

    /// <summary>
    /// Renders a quick preview PNG. Always uses the built-in renderer (no third-party warm-up, always
    /// available) regardless of the selected export backend, and reports no progress — it's a best-effort
    /// thumbnail for the window, not the final export.
    /// </summary>
    public static byte[] RenderPreview(FeatureCollection features, RenderSettings render) =>
        MapRenderer.RenderPng(features, RenderOptionsFor(render, null));

    public static string RenderSvg(FeatureCollection features, RenderSettings render, IProgress<ConvertProgress>? progress = null) =>
        MapRenderer.RenderSvg(features, RenderOptionsFor(render, progress));

    // Common name-like property keys, tried in order, so the "Labels" toggle works without the user
    // having to name a property — mirrors the Blazor app. Falls back to the feature id.
    static readonly string[] labelKeys =
        ["name", "NAME", "Name", "name_en", "NAME_EN", "admin", "ADMIN", "title", "label", "id"];

    static string? AutoLabel(Feature feature)
    {
        foreach (var key in labelKeys)
        {
            if (feature.Properties.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return feature.Id?.ToString();
    }

    /// <summary>Maps a <see cref="RenderSettings"/> onto a <see cref="RenderOptions"/>.</summary>
    public static RenderOptions RenderOptionsFor(RenderSettings settings, IProgress<ConvertProgress>? progress)
    {
        var options = new RenderOptions
        {
            Projection = settings.Projection,
            Bounds = settings.Bounds,
            Padding = settings.Padding,
            Background = settings.Background,
            Stroke = settings.Stroke,
            Fill = settings.Fill,
            StrokeWidth = settings.StrokeWidth,
            PointRadius = settings.PointRadius,
            StrokeAutoScale = settings.StrokeAutoScale,
            LabelSize = settings.LabelSize,
            LabelColor = settings.LabelColor,
            // Ocean paints the projection's world envelope under every feature (the lobes for Goode, the
            // whole canvas otherwise) — see the Blazor service for the full rationale. On by default.
            Ocean = settings.OceanEnabled ? settings.Ocean : null,
            LabelHalo = settings.HaloEnabled ? settings.LabelHalo : null,
            LabelKnockout = settings.KnockoutEnabled ? settings.LabelKnockout : null,
            MinFeaturePixels = settings.MinFeaturePixels,
            Png = new() { Compression = settings.PngCompression },
            Svg = new() { SimplifyTolerance = settings.SvgSimplifyTolerance },
            Progress = progress,
        };

        // MaxDimension (fit-to-box) wins when set; otherwise an explicit width with a derived or explicit height.
        if (settings.MaxDimension > 0)
        {
            options.MaxDimension = settings.MaxDimension;
        }
        else
        {
            if (settings.Width > 0)
            {
                options.Width = settings.Width;
            }

            options.Height = settings.Height;
        }

        if (settings.Labels)
        {
            if (string.IsNullOrWhiteSpace(settings.LabelProperty))
            {
                options.Label = AutoLabel;
            }
            else
            {
                var key = settings.LabelProperty;
                options.Label = _ =>
                    _.Properties.TryGetValue(key, out var value) && value is not null
                        ? value.ToString()
                        : null;
            }
        }

        return options;
    }

    /// <summary>Builds an open/save dialog filter: an "All supported" clause, every format, then "All files".</summary>
    public static string BuildDialogFilter(IReadOnlyList<FormatInfo> formats)
    {
        var clauses = new List<string>();
        var allPatterns = string.Join(
            ';',
            formats.SelectMany(_ => _.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).Select(_ => $"*{_}"));
        clauses.Add($"All supported ({allPatterns})|{allPatterns}");
        clauses.AddRange(formats.Select(_ => _.DialogFilter));
        clauses.Add("All files (*.*)|*.*");
        return string.Join('|', clauses);
    }
}
