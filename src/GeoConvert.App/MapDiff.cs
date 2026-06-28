using System.Drawing.Imaging;

namespace GeoConvert.App;

/// <summary>
/// Compares two maps. The visual diff reuses the existing renderer with zero new drawing code: an
/// <see cref="DiffMode.Overlay"/> stacks both collections on one canvas via the multi-collection
/// <see cref="MapRenderer.RenderPng(IReadOnlyList{FeatureCollection}, RenderOptions)"/> overload with a
/// per-layer <see cref="LayerStyle"/> colouring A and B differently (so shared geometry blends and
/// differences read as pure A- or B-colour), and <see cref="DiffMode.SideBySide"/> renders each map to
/// the same union extent and composites them. The structural <see cref="Summarize"/> reports feature
/// counts, geometry-type histograms, bounds, property keys and their deltas — the headline of a
/// command-line diff. Inputs are flattened (layers collapsed) for the visual diff so per-layer styling
/// stays correct on layered sources; <see cref="Summarize"/> still reports the original layer counts.
/// </summary>
public static class MapDiff
{
    public static Rgba DefaultColorA { get; } = new(200, 30, 30);

    public static Rgba DefaultColorB { get; } = new(40, 90, 210);

    /// <summary>Renders the diff image for <paramref name="mode"/> as PNG bytes.</summary>
    public static byte[] Render(
        FeatureCollection a,
        FeatureCollection b,
        RenderSettings settings,
        DiffMode mode,
        Rgba colorA,
        Rgba colorB) =>
        mode == DiffMode.SideBySide
            ? RenderSideBySide(a, b, settings, colorA, colorB)
            : RenderOverlay(a, b, settings, colorA, colorB);

    static byte[] RenderOverlay(FeatureCollection a, FeatureCollection b, RenderSettings settings, Rgba colorA, Rgba colorB)
    {
        var flatA = Flatten(a);
        var flatB = Flatten(b);

        var options = ConversionService.RenderOptionsFor(settings, null);
        // The ocean fill would paint a solid layer over one of the maps; labels would clutter the
        // comparison. Both off for the diff, regardless of the user's export settings.
        options.Ocean = null;
        options.Label = null;
        options.LayerStyle = layer =>
        {
            if (ReferenceEquals(layer, flatA))
            {
                return StyleFor(colorA);
            }

            if (ReferenceEquals(layer, flatB))
            {
                return StyleFor(colorB);
            }

            return null;
        };

        // Bounds left as the user set them (null => the renderer unions both inputs), so the two layers
        // share one extent and line up.
        return MapRenderer.RenderPng([flatA, flatB], options);
    }

    static byte[] RenderSideBySide(FeatureCollection a, FeatureCollection b, RenderSettings settings, Rgba colorA, Rgba colorB)
    {
        var flatA = Flatten(a);
        var flatB = Flatten(b);

        // Force both panels onto the same extent so they are spatially comparable: the user's bbox if
        // set, otherwise the union of both maps.
        var bounds = settings.Bounds ?? flatA.GetBounds().ExpandToInclude(flatB.GetBounds());
        if (bounds.IsEmpty)
        {
            throw new GeoConvertException("Cannot render a side-by-side diff: neither map has a spatial extent.");
        }

        var bytesA = RenderPanel(flatA, settings, bounds, colorA);
        var bytesB = RenderPanel(flatB, settings, bounds, colorB);

        using var imageA = LoadBitmap(bytesA);
        using var imageB = LoadBitmap(bytesB);

        const int gap = 8;
        var width = imageA.Width + gap + imageB.Width;
        var height = Math.Max(imageA.Height, imageB.Height);
        using var combined = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(combined))
        {
            graphics.Clear(settings.Background.ToColor());
            graphics.DrawImage(imageA, 0, 0);
            graphics.DrawImage(imageB, imageA.Width + gap, 0);
        }

        using var output = new MemoryStream();
        combined.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    static byte[] RenderPanel(FeatureCollection collection, RenderSettings settings, Envelope bounds, Rgba color)
    {
        var options = ConversionService.RenderOptionsFor(settings, null);
        options.Bounds = bounds;
        options.Ocean = null;
        options.Label = null;
        options.Stroke = color;
        options.Fill = color with { A = 70 };
        return MapRenderer.RenderPng(collection, options);
    }

    static LayerStyle StyleFor(Rgba color) =>
        new()
        {
            Stroke = color,
            Fill = color with { A = 70 },
        };

    static Bitmap LoadBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return new(stream);
    }

    // Collapse a (possibly layered) collection into a single flat layer of all its features. The visual
    // diff styles per top-level layer, so flattening keeps A and B each a single styled layer even when
    // the source had folders/sub-layers.
    static FeatureCollection Flatten(FeatureCollection collection) =>
        new(collection)
        {
            Name = collection.Name,
        };

    /// <summary>Builds the human-readable structural comparison printed by the CLI and shown in the diff view.</summary>
    public static string Summarize(string nameA, FeatureCollection a, string nameB, FeatureCollection b)
    {
        var statsA = MapStats.Analyze(a);
        var statsB = MapStats.Analyze(b);

        var builder = new StringBuilder();
        AppendMap(builder, "Map A", nameA, statsA);
        builder.AppendLine();
        AppendMap(builder, "Map B", nameB, statsB);
        builder.AppendLine();
        AppendDifferences(builder, statsA, statsB);
        return builder.ToString();
    }

    static void AppendMap(StringBuilder builder, string label, string name, MapStats stats)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"{label}: {name}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Features:   {stats.FeatureCount}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Layers:     {stats.LayerCount}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Bounds:     {FormatBounds(stats.Bounds)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Geometry:   {FormatKinds(stats.GeometryKinds)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Properties: {FormatKeys(stats.PropertyKeys)}");
    }

    static void AppendDifferences(StringBuilder builder, MapStats a, MapStats b)
    {
        builder.AppendLine("Differences:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Features:   {FormatDelta(b.FeatureCount - a.FeatureCount)}");

        var kindLines = new List<string>();
        foreach (var kind in a.GeometryKinds.Keys.Union(b.GeometryKinds.Keys).OrderBy(_ => _, StringComparer.Ordinal))
        {
            var delta = b.GeometryKinds.GetValueOrDefault(kind) - a.GeometryKinds.GetValueOrDefault(kind);
            if (delta != 0)
            {
                kindLines.Add($"{kind} {FormatDelta(delta)}");
            }
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"  Geometry:   {(kindLines.Count == 0 ? "(no change)" : string.Join(", ", kindLines))}");

        var onlyInA = a.PropertyKeys.Except(b.PropertyKeys, StringComparer.Ordinal).OrderBy(_ => _, StringComparer.Ordinal).ToList();
        var onlyInB = b.PropertyKeys.Except(a.PropertyKeys, StringComparer.Ordinal).OrderBy(_ => _, StringComparer.Ordinal).ToList();
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Properties only in A: {(onlyInA.Count == 0 ? "(none)" : string.Join(", ", onlyInA))}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Properties only in B: {(onlyInB.Count == 0 ? "(none)" : string.Join(", ", onlyInB))}");
    }

    static string FormatDelta(int value) =>
        value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

    static string FormatBounds(Envelope bounds) =>
        bounds.IsEmpty
            ? "(empty)"
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.###}, {1:0.###} .. {2:0.###}, {3:0.###}",
                bounds.MinX,
                bounds.MinY,
                bounds.MaxX,
                bounds.MaxY);

    static string FormatKinds(IReadOnlyDictionary<string, int> kinds) =>
        kinds.Count == 0
            ? "(none)"
            : string.Join(", ", kinds.OrderByDescending(_ => _.Value).Select(_ => $"{_.Key} {_.Value}"));

    static string FormatKeys(IReadOnlyCollection<string> keys) =>
        keys.Count == 0 ? "(none)" : string.Join(", ", keys);

    sealed record MapStats(
        int FeatureCount,
        int LayerCount,
        Envelope Bounds,
        IReadOnlyDictionary<string, int> GeometryKinds,
        IReadOnlyCollection<string> PropertyKeys)
    {
        public static MapStats Analyze(FeatureCollection collection)
        {
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var feature in collection)
            {
                var kind = KindOf(feature.Geometry);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                foreach (var key in feature.Properties.Keys)
                {
                    keys.Add(key);
                }
            }

            return new(collection.Count, CountLayers(collection), collection.GetBounds(), kinds, keys);
        }

        static int CountLayers(FeatureCollection collection)
        {
            var total = 1;
            foreach (var child in collection.Children)
            {
                total += CountLayers(child);
            }

            return total;
        }

        static string KindOf(Geometry? geometry) =>
            geometry switch
            {
                null => "(none)",
                Point => "Point",
                LineString => "LineString",
                Polygon => "Polygon",
                MultiPoint => "MultiPoint",
                MultiLineString => "MultiLineString",
                MultiPolygon => "MultiPolygon",
                GeometryCollection => "GeometryCollection",
                _ => geometry.GetType().Name,
            };
    }
}
