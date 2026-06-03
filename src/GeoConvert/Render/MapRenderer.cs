namespace GeoConvert;

/// <summary>
/// Renders a <see cref="FeatureCollection"/> to a PNG raster, clipped to a bounding box. This is a
/// write-only export (a PNG cannot be read back into features). Built on a small software rasterizer and
/// a hand-rolled PNG encoder, with no third-party dependencies.
/// </summary>
public static class MapRenderer
{
    // Standard Web Mercator latitude cutoff: ln(tan) blows up at ±90°, and ±85.0511° is where the
    // projected square world meets its longitudinal width — the convention every tile provider uses.
    // Internal (not private) so the Projection class, split into its own file, can clamp to it.
    internal const double WebMercatorMaxLatitude = 85.05112877980659;

    /// <summary>
    /// The conventional <see cref="RenderOptions.Bounds"/> for a Web Mercator world map: longitude spans
    /// the full ±180° and latitude is the ±85.0511° cutoff that makes the projected world a 1:1 square,
    /// matching the layout used by every tiled-map provider. Pass this when rendering a global view
    /// under <see cref="MapProjection.WebMercator"/>; for any subregion just supply real data bounds.
    /// </summary>
    public static Envelope WebMercatorWorldBounds { get; } =
        new(-180, -WebMercatorMaxLatitude, 180, WebMercatorMaxLatitude);

    public static byte[] RenderPng(FeatureCollection features, RenderOptions? options = null) =>
        RenderPng([features], options);

    public static void RenderPng(FeatureCollection features, string path, RenderOptions? options = null) =>
        RenderPng([features], path, options);

    public static void RenderPng(FeatureCollection features, Stream stream, RenderOptions? options = null) =>
        RenderPng([features], stream, options);

    /// <summary>
    /// Renders multiple <see cref="FeatureCollection"/>s as stacked top-level layers, in order — the
    /// first paints under, the last on top under source-over blending. Each collection is treated as
    /// its own layer for <see cref="RenderOptions.LayerStyle"/> (and any nested
    /// <see cref="FeatureCollection.Children"/> still recurse from there). When
    /// <see cref="RenderOptions.Bounds"/> is null the rendered extent is the union of all input
    /// collections.
    /// </summary>
    public static byte[] RenderPng(IReadOnlyList<FeatureCollection> layers, RenderOptions? options = null)
    {
        options ??= new();
        var bounds = Validate(layers, options);
        using var memory = new MemoryStream();
        RenderWithProgress(layers, memory, options, bounds);
        return memory.ToArray();
    }

    public static void RenderPng(IReadOnlyList<FeatureCollection> layers, string path, RenderOptions? options = null)
    {
        options ??= new();
        // Validate before File.Create so a throw leaves the destination untouched instead of stranding
        // a 0-byte file. Mid-render stream failures (disk full, etc.) can still leave a partial file,
        // but those are unrecoverable I/O errors where a partial file is the conventional signal.
        var bounds = Validate(layers, options);
        using var stream = File.Create(path);
        RenderWithProgress(layers, stream, options, bounds);
    }

    public static void RenderPng(IReadOnlyList<FeatureCollection> layers, Stream stream, RenderOptions? options = null)
    {
        options ??= new();
        var bounds = Validate(layers, options);
        RenderWithProgress(layers, stream, options, bounds);
    }

    // Used by GeoConverter when a PNG conversion is given a progress sink: the facade has already built
    // the reporter (so the Writing phase is shared with the preceding read) and wrapped the stream for
    // byte tracking, so render straight through rather than going via RenderOptions.Progress.
    internal static void RenderPng(FeatureCollection features, Stream stream, ProgressReporter progress)
    {
        var layers = new[] { features };
        var options = new RenderOptions();
        var bounds = Validate(layers, options);
        Render(layers, stream, options, bounds, progress);
    }

    // Honours RenderOptions.Progress for the public entry points: builds a Writing-phase reporter whose
    // FeatureTotal is the whole layer set, wraps the stream so the encoded PNG bytes are tallied too,
    // then renders. With no sink set it renders straight through with no per-feature bookkeeping.
    static void RenderWithProgress(IReadOnlyList<FeatureCollection> layers, Stream stream, RenderOptions options, Envelope bounds)
    {
        if (options.Progress is { } sink)
        {
            var reporter = new ProgressReporter(sink, ProgressPhase.Writing, TotalFeatures(layers), null);
            Render(layers, new ProgressStream(stream, reporter), options, bounds, reporter);
        }
        else
        {
            Render(layers, stream, options, bounds, null);
        }
    }

    static long TotalFeatures(IReadOnlyList<FeatureCollection> layers)
    {
        long total = 0;
        foreach (var layer in layers)
        {
            total += layer.Count;
        }

        return total;
    }

    static Envelope Validate(IReadOnlyList<FeatureCollection> layers, RenderOptions options)
    {
        var bounds = options.Bounds ?? UnionBounds(layers);
        if (bounds.IsEmpty)
        {
            throw new GeoConvertException(
                "Cannot render PNG: the features is empty. Provide RenderOptions.Bounds.");
        }

        if (options is {MaxDimension: <= 0, Width: <= 0})
        {
            throw new GeoConvertException("RenderOptions.Width must be positive.");
        }

        return bounds;
    }

    static Envelope UnionBounds(IReadOnlyList<FeatureCollection> layers)
    {
        var bounds = Envelope.Empty;
        foreach (var layer in layers)
        {
            bounds = bounds.ExpandToInclude(layer.GetBounds());
        }

        return bounds;
    }

    static void Render(IReadOnlyList<FeatureCollection> layers, Stream stream, RenderOptions options, Envelope bounds, ProgressReporter? progress)
    {
        var projection = new Projection(bounds, options);
        using var canvas = new Canvas(projection.Width, projection.Height, options.Background);

        // StrokeAutoScale: derive a multiplier from the implicit zoom (canvas/bbox ratio) so the
        // same scene rendered at a tighter bbox or bigger canvas gets proportionally thicker
        // strokes, matching what tile-map stylesheets do across zoom levels. When the flag is
        // off, the multiplier is 1.0 — the threading is the same in both cases so there's no
        // branch on every feature.
        var strokeMultiplier = options.StrokeAutoScale ? ComputeStrokeMultiplier(canvas, bounds) : 1.0;

        if (options.Ocean is { } ocean)
        {
            // Paint the projection envelope first so every feature layer renders on top of it. For
            // Goode this fills each lobe with the ocean colour, leaving the inter-lobe gaps as the
            // canvas background — that's what makes the projection's lobed shape pop visually.
            foreach (var ring in projection.GetWorldEnvelopeRings())
            {
                canvas.FillPolygon([ring], ocean);
            }

            // Outline each lobe with the regular stroke colour so the envelope reads as a clear
            // border around the world even where the inside is bare ocean. For Goode the equator
            // edge is intentionally omitted from the stroke (otherwise the north and south lobes'
            // top/bottom edges would double up into a thick horizontal line bisecting the map).
            foreach (var chain in projection.GetWorldEnvelopeStrokes())
            {
                for (var i = 0; i + 1 < chain.Length; i++)
                {
                    canvas.StrokeLine(chain[i].X, chain[i].Y, chain[i + 1].X, chain[i + 1].Y, options.StrokeWidth * strokeMultiplier, options.Stroke);
                }
            }
        }

        foreach (var layer in layers)
        {
            DrawLayer(canvas, layer, projection, options, strokeMultiplier, progress);
        }

        // Labels run after every geometry pass so they sit on top of all fills and strokes —
        // burying a label under a later layer's fill would defeat the point. A single Labeller is
        // shared across every layer so collisions are global: a child layer's label can't overlap a
        // parent layer's label, even though their geometry passes paint independently.
        var labeller = new Labeller(canvas);
        foreach (var layer in layers)
        {
            DrawLabels(layer, projection, options, labeller, strokeMultiplier);
        }

        Png.Write(stream, canvas.Pixels, canvas.Width, canvas.Height, options.Compression);
    }

    // Pre-order: a layer paints its own features first, then recurses into its children. Source-over
    // blending means whatever paints last sits on top, so children naturally appear over their parent
    // — pick layer styles via RenderOptions.LayerStyle to keep them visually distinct.
    static void DrawLayer(Canvas canvas, FeatureCollection layer, Projection projection, RenderOptions options, double strokeMultiplier, ProgressReporter? progress)
    {
        var style = Resolve(options.LayerStyle?.Invoke(layer), options, strokeMultiplier);
        foreach (var feature in layer.Features)
        {
            if (feature.Geometry is { } geometry)
            {
                Draw(canvas, geometry, projection, style);
            }

            // One report per feature visited (geometry or not) so the running count reaches the
            // FeatureTotal the reporter was seeded with. The label pass below doesn't re-report.
            progress?.Feature();
        }

        foreach (var child in layer.Children)
        {
            DrawLayer(canvas, child, projection, options, strokeMultiplier, progress);
        }
    }

    // Collapses the user-facing LayerStyle (any subset of overrides) into the concrete values the
    // rasterizer needs, falling back to RenderOptions defaults for each null property independently.
    // StrokeWidth scales by the full zoom-derived strokeMultiplier; PointRadius scales by the gentler
    // PointMultiplier (see remarks there). Both are 1.0 unless StrokeAutoScale is on.
    // MinFeaturePixels is NOT multiplied by anything — it's a flat pixel threshold the rasterizer
    // tests against projected bbox sizes directly, so a value of 1 means "1 pixel" regardless of
    // zoom (and 0 means "no filter", the default).
    static ResolvedStyle Resolve(LayerStyle? overrides, RenderOptions options, double strokeMultiplier) =>
        new(
            overrides?.Stroke ?? options.Stroke,
            overrides?.Fill ?? options.Fill,
            (overrides?.StrokeWidth ?? options.StrokeWidth) * strokeMultiplier,
            (overrides?.PointRadius ?? options.PointRadius) * PointMultiplier(strokeMultiplier),
            overrides?.MinFeaturePixels ?? options.MinFeaturePixels);

    // Point markers scale more gently than line strokes. The √2 stroke ramp is deliberately steep so
    // dense borders thin to faint hairlines at thumbnail/world scale (the whole point of the curve),
    // but applying that same ramp to point radii shrinks city/town dots to near-invisible specks at
    // country scale — where a dot still has to read as a dot. So split the difference: average the
    // stroke multiplier with 1.0 (the un-scaled, fixed-pixel size), pulling the marker halfway back
    // toward its base radius. At strokeMultiplier 1 (autoscale off, or exactly at the anchor zoom)
    // this is a no-op, so fixed-pixel output stays bit-identical; below the anchor dots stay legible
    // instead of vanishing, above it they still grow, just less aggressively than the borders.
    static double PointMultiplier(double strokeMultiplier) =>
        (strokeMultiplier + 1) / 2;

    // Stroke-width multiplier curve. The multiplier halves for every two implicit zoom levels below
    // the country-scale anchor and doubles for every two above it — base √2 per zoom level. That's a
    // far steeper ramp than the tile-map ~1.15×/level convention, on purpose: a *static* render of a
    // fixed dataset wants its strokes roughly proportional to the output's pixel density (halve the
    // canvas → halve the stroke), so a thumbnail of a dense map reads as thin faint hairlines instead
    // of a black mass, while a tightly-zoomed render gets substantial borders. The lower clamp is low
    // enough that a whole-world thumbnail still thins right down (StrokeLine's coverage compensation
    // then renders that as a faint hairline rather than nothing); the upper clamp caps street-level
    // zooms before the stroke swallows the canvas.
    const double strokeZoomBase = 1.4142135623730951; // √2
    const int strokeZoomAnchor = 10;
    const double strokeMultiplierMin = 0.1;
    const double strokeMultiplierMax = 6;

    /// <summary>
    /// Derives a stroke-width multiplier from the canvas/bbox ratio — the static-render equivalent
    /// of tile-map zoom-aware styling. Uses the smaller of the horizontal and vertical
    /// pixels-per-degree (the axis that actually fits the rendered extent), converts to an
    /// implicit zoom via the tile-map convention (zoom = log2(width-at-360° / 256)), then scales the
    /// multiplier by <see cref="strokeZoomBase"/> (√2) per zoom level with zoom
    /// <see cref="strokeZoomAnchor"/> (country-scale) as the multiplier-of-1 baseline. Clamped to
    /// [<see cref="strokeMultiplierMin"/>, <see cref="strokeMultiplierMax"/>] so a degenerate bbox
    /// doesn't blow the multiplier to infinity or zero.
    /// </summary>
    static double ComputeStrokeMultiplier(Canvas canvas, Envelope bounds)
    {
        var pixelsPerDegree = Math.Min(canvas.Width / bounds.Width, canvas.Height / bounds.Height);
        var zoom = Math.Log2(pixelsPerDegree * 360.0 / 256);
        var multiplier = Math.Pow(strokeZoomBase, zoom - strokeZoomAnchor);
        return Math.Clamp(multiplier, strokeMultiplierMin, strokeMultiplierMax);
    }

    static void Draw(Canvas canvas, Geometry geometry, Projection projection, ResolvedStyle style)
    {
        switch (geometry)
        {
            case Point point:
                var (px, py) = projection.ToPixel(point.Coordinate);
                canvas.FillDisc(px, py, style.PointRadius, style.Stroke);
                break;
            case MultiPoint multiPoint:
                foreach (var position in multiPoint.Positions)
                {
                    var (x, y) = projection.ToPixel(position);
                    canvas.FillDisc(x, y, style.PointRadius, style.Stroke);
                }

                break;
            case LineString line:
                StrokePath(canvas, line.Positions, projection, style);
                break;
            case MultiLineString multiLine:
                foreach (var child in multiLine.LineStrings)
                {
                    StrokePath(canvas, child.Positions, projection, style);
                }

                break;
            case Polygon polygon:
                DrawPolygon(canvas, polygon, projection, style);
                break;
            case MultiPolygon multiPolygon:
                foreach (var child in multiPolygon.Polygons)
                {
                    DrawPolygon(canvas, child, projection, style);
                }

                break;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    Draw(canvas, child, projection, style);
                }

                break;
        }
    }

    static void DrawPolygon(Canvas canvas, Polygon polygon, Projection projection, ResolvedStyle style)
    {
        // MinFeaturePixels: render-time cartographic selection. Drop this whole polygon — including
        // its holes — if its outer ring's projected pixel bbox is below the threshold in both axes,
        // so a country's tiny offshore islands disappear at world scale while its mainland (huge
        // bbox) still paints. Holes are skipped along with their owner because a hole inside a
        // dropped polygon has nothing to cut into. Applied per Polygon, NOT per MultiPolygon, since
        // the Draw switch routes each MultiPolygon member here individually — exactly what we want
        // for an archipelago country whose mainland survives and skerries don't.
        if (style.MinFeaturePixels > 0
            && polygon.Rings.Count > 0
            && IsBelowMinPixels(polygon.Rings[0], projection, style.MinFeaturePixels))
        {
            return;
        }

        // PreparePolygon yields one batch per output piece — for Goode that's one per lobe with
        // content. Fill uses the clipped closed rings (so the lobe-boundary closure participates
        // in even-odd fill), while strokes use the open polyline chains that omit any
        // clip-boundary edges — otherwise a clipped continent like Antarctica would render with a
        // dark vertical stroke down each lobe meridian, reading as a thin slice through the shape.
        foreach (var batch in projection.PreparePolygon(polygon.Rings))
        {
            canvas.FillPolygon(batch.Fill, style.Fill);
            foreach (var chain in batch.Strokes)
            {
                StrokeRing(canvas, chain, style);
            }
        }
    }

    static void StrokePath(Canvas canvas, IReadOnlyList<Position> positions, Projection projection, ResolvedStyle style)
    {
        // MinFeaturePixels filter, mirroring DrawPolygon. Applied per LineString — the Draw switch
        // routes each MultiLineString member through here independently — so a major river's huge
        // main channel renders while sub-pixel tributaries don't. A point-degenerate line (all
        // vertices coincide) has a 0-pixel bbox and is filtered out for any positive threshold;
        // that's correct, points are handled by their own switch case if the caller wanted them.
        if (style.MinFeaturePixels > 0 && IsBelowMinPixels(positions, projection, style.MinFeaturePixels))
        {
            return;
        }

        // PrepareLine yields one subpath per lobe the line crosses (just the input line itself for
        // non-interrupted projections). Each subpath stays in one lobe so consecutive vertices
        // never straddle the interrupt gap.
        foreach (var subpath in projection.PrepareLine(positions))
        {
            for (var i = 0; i + 1 < subpath.Length; i++)
            {
                canvas.StrokeLine(subpath[i].X, subpath[i].Y, subpath[i + 1].X, subpath[i + 1].Y, style.StrokeWidth, style.Stroke);
            }
        }
    }

    // True if the projected pixel bbox of the supplied vertex sequence (a polygon's outer ring or a
    // line's positions) is below `minPixels` in both axes — meaning the feature paints into less
    // than minPixels × minPixels and so doesn't earn its visual weight at this scale.
    //
    // Cheap path: walk the vertices once to get the lon/lat bbox, then project the four corners.
    // For linear projections (PlateCarree, WebMercator) the projected bbox of the lon/lat bbox is
    // exactly the bbox of the projected ring. For non-linear projections (Lambert, Goode) it's an
    // approximation, but for the sub-pixel features this filter exists to catch the projection is
    // locally linear (a few km of latitude span) and the approximation is very tight. The
    // alternative — project every vertex — costs O(vertices) for every feature, and the answer is
    // identical for the small features we're filtering, so the corner-only test is the right
    // engineering trade. Returns false for an empty sequence (nothing to filter).
    static bool IsBelowMinPixels(IReadOnlyList<Position> positions, Projection projection, double minPixels)
    {
        if (positions.Count == 0)
        {
            return false;
        }

        double minLon = positions[0].X, maxLon = minLon, minLat = positions[0].Y, maxLat = minLat;
        for (var i = 1; i < positions.Count; i++)
        {
            var p = positions[i];
            if (p.X < minLon) minLon = p.X;
            else if (p.X > maxLon) maxLon = p.X;
            if (p.Y < minLat) minLat = p.Y;
            else if (p.Y > maxLat) maxLat = p.Y;
        }

        var (x1, y1) = projection.ToPixel(new(minLon, minLat));
        var (x2, y2) = projection.ToPixel(new(maxLon, minLat));
        var (x3, y3) = projection.ToPixel(new(minLon, maxLat));
        var (x4, y4) = projection.ToPixel(new(maxLon, maxLat));

        var pxMin = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
        var pxMax = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
        var pyMin = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        var pyMax = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));

        // Strict <: a feature exactly minPixels across renders (the user asked to drop things
        // *below* the threshold). max() rather than min(): a feature is kept if EITHER axis meets
        // the threshold, dropped only when both are sub-threshold. A long thin coastline 1px wide
        // and 200px long survives at minPixels=4; a 1×1 px island doesn't.
        return Math.Max(pxMax - pxMin, pyMax - pyMin) < minPixels;
    }

    static void StrokeRing(Canvas canvas, (double X, double Y)[] ring, ResolvedStyle style)
    {
        for (var i = 0; i + 1 < ring.Length; i++)
        {
            canvas.StrokeLine(ring[i].X, ring[i].Y, ring[i + 1].X, ring[i + 1].Y, style.StrokeWidth, style.Stroke);
        }
    }

    // Pre-order walk matching DrawLayer's order: a parent's labels are placed before its children's,
    // so on collision the higher-up-the-tree label wins. That mirrors the typical cartographic
    // hierarchy (country labels outrank state labels outrank city labels) when callers build their
    // layer tree from coarse-to-fine.
    static void DrawLabels(FeatureCollection layer, Projection projection, RenderOptions options, Labeller labeller, double strokeMultiplier)
    {
        var style = ResolveLabel(options.LayerStyle?.Invoke(layer), options, strokeMultiplier);
        if (style.Label != null)
        {
            // Process features highest-priority-first within the layer. When the caller provided
            // a priority callback, it decides — typical pattern is to pull from a feature
            // property (population, label-rank, etc.) or capture a lookup table in the closure.
            // Without one, fall back to the default geometric rule: polygon area / line length /
            // points last, so on overlap the bigger feature anchors its label first. Greedy
            // collision then drops the loser.
            //
            // Ties (equal priority) are broken by label text rather than by input order. Relying on
            // a stable sort to "preserve file order" only yields a deterministic image if the caller
            // always supplies features in the same order — and they often don't: feature order
            // routinely comes from HashSet/Dictionary enumeration, which .NET randomises per process.
            // Breaking ties on a value intrinsic to the feature makes placement a pure function of
            // the features, so the same map renders byte-identically regardless of enumeration order.
            var label = style.Label;
            var priorityFn = style.Priority ?? (Func<Feature, double>)(_ => LabelPriority(_.Geometry));
            var sorted = layer.Features
                .OrderByDescending(priorityFn)
                .ThenBy(_ => label(_) ?? "", StringComparer.Ordinal);
            foreach (var feature in sorted)
            {
                if (feature.Geometry is not { } geometry)
                {
                    continue;
                }

                var text = style.Label(feature);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (ComputeAnchor(geometry, projection) is not { } anchor)
                {
                    continue;
                }

                // Point anchors get an Imhof candidate ring around the dot — pointOffset is
                // the gap between the dot edge and the nearer label edge. PointRadius reads
                // straight out of the resolved style (already × strokeMultiplier), plus a
                // small constant pad so the label doesn't kiss the dot. Polygon and line
                // anchors pass pointOffset=0 → Labeller centres the label on the anchor,
                // which is what the interior of a feature should do.
                var pointOffset = anchor.Kind == AnchorKind.Point ? style.PointRadius + 2 : 0;
                labeller.TryPlace(text, anchor.X, anchor.Y, style.Size, style.Color, style.Halo, pointOffset, style.Knockout);
            }
        }

        foreach (var child in layer.Children)
        {
            DrawLabels(child, projection, options, labeller, strokeMultiplier);
        }
    }

    // Relative "how important is this label" score used to order placement within a layer. Computed
    // in lon/lat so it's projection-independent — the absolute number is meaningless, only the
    // ordering matters. Polygon area trumps line length trumps point (constant 0) on the
    // assumption that bigger map features carry more important names; that aligns with the
    // common cartographic convention of country > state > city. GeometryCollection takes the max
    // of its children so a country represented as Polygon+annotations still ranks like a country.
    static double LabelPriority(Geometry? geometry)
    {
        switch (geometry)
        {
            case Polygon polygon:
                return polygon.Rings.Count == 0 ? 0 : Math.Abs(Ring.SignedArea(polygon.Rings[0]));
            case MultiPolygon multiPolygon:
                var total = 0d;
                foreach (var p in multiPolygon.Polygons)
                {
                    if (p.Rings.Count > 0)
                    {
                        total += Math.Abs(Ring.SignedArea(p.Rings[0]));
                    }
                }

                return total;
            case LineString line:
                return PathLength(line.Positions);
            case MultiLineString multiLine:
                var length = 0d;
                foreach (var l in multiLine.LineStrings)
                {
                    length += PathLength(l.Positions);
                }

                return length;
            case GeometryCollection collection:
                var best = 0d;
                foreach (var child in collection.Geometries)
                {
                    var priority = LabelPriority(child);
                    if (priority > best)
                    {
                        best = priority;
                    }
                }

                return best;
            default:
                return 0;
        }
    }

    static double PathLength(IReadOnlyList<Position> positions)
    {
        var total = 0d;
        for (var i = 0; i + 1 < positions.Count; i++)
        {
            total += SegmentLength(positions[i], positions[i + 1]);
        }

        return total;
    }

    // Mirrors Resolve for the label knobs: per-layer overrides take precedence, falling back to the
    // RenderOptions defaults independently per property. Label itself can be left null on the layer
    // to inherit the options-wide default (the typical "label every layer using this property" case).
    // PointRadius is folded in (multiplied by strokeMultiplier exactly as the geometry pass does it)
    // so DrawLabels can size the Imhof candidate ring's offset to clear the dot the renderer drew.
    static ResolvedLabelStyle ResolveLabel(LayerStyle? overrides, RenderOptions options, double strokeMultiplier) =>
        new(
            overrides?.Label ?? options.Label,
            overrides?.LabelSize ?? options.LabelSize,
            overrides?.LabelColor ?? options.LabelColor,
            overrides?.LabelHalo ?? options.LabelHalo,
            overrides?.LabelKnockout ?? options.LabelKnockout,
            overrides?.LabelPriority ?? options.LabelPriority,
            (overrides?.PointRadius ?? options.PointRadius) * strokeMultiplier);

    // Pixel-space anchor for a label, paired with its kind so DrawLabels knows whether to centre
    // the label (Area: polygon centroid / line midpoint) or walk the Imhof ring around it (Point:
    // dot, multi-point first vertex). Polygons use the signed-area-weighted centroid of their
    // outer ring; lines use the arclength midpoint; multi-* picks the largest sub-piece (so a
    // multi-polygon country like New Zealand labels on the North Island, not Stewart Island).
    // For non-linear projections (Lambert, Goode) the centroid is computed in lon/lat then
    // projected — strictly that's not the projected centroid, but it's the right ballpark for
    // label placement at this fidelity. GeometryCollection descends into its first member with a
    // usable anchor and inherits that child's kind.
    static AnchorPoint? ComputeAnchor(Geometry geometry, Projection projection)
    {
        switch (geometry)
        {
            case Point point:
            {
                var (px, py) = projection.ToPixel(point.Coordinate);
                return new AnchorPoint(px, py, AnchorKind.Point);
            }

            case MultiPoint multiPoint:
                if (multiPoint.Positions.Count == 0)
                {
                    return null;
                }

                var (mx, my) = projection.ToPixel(multiPoint.Positions[0]);
                return new AnchorPoint(mx, my, AnchorKind.Point);

            case LineString line:
                if (LineAnchor(line.Positions, projection) is { } lineAnchor)
                {
                    return new AnchorPoint(lineAnchor.X, lineAnchor.Y, AnchorKind.Area);
                }

                return null;

            case MultiLineString multiLine:
                if (LongestLineAnchor(multiLine.LineStrings, projection) is { } multiLineAnchor)
                {
                    return new AnchorPoint(multiLineAnchor.X, multiLineAnchor.Y, AnchorKind.Area);
                }

                return null;

            case Polygon polygon:
                if (polygon.Rings.Count == 0)
                {
                    return null;
                }

                if (PolygonAnchor(polygon.Rings[0], projection) is { } polygonAnchor)
                {
                    return new AnchorPoint(polygonAnchor.X, polygonAnchor.Y, AnchorKind.Area);
                }

                return null;

            case MultiPolygon multiPolygon:
                if (LargestPolygonAnchor(multiPolygon.Polygons, projection) is { } multiPolygonAnchor)
                {
                    return new AnchorPoint(multiPolygonAnchor.X, multiPolygonAnchor.Y, AnchorKind.Area);
                }

                return null;

            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    if (ComputeAnchor(child, projection) is { } anchor)
                    {
                        return anchor;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    static (double X, double Y)? LineAnchor(IReadOnlyList<Position> positions, Projection projection)
    {
        if (positions.Count == 0)
        {
            return null;
        }

        if (positions.Count == 1)
        {
            return projection.ToPixel(positions[0]);
        }

        // Total length first; midpoint is the position at half the cumulative arclength. Computed
        // in lon/lat — for non-linear projections the projected midpoint of a long line could drift
        // off the line slightly, but for the per-segment lengths typical of real geodata the
        // difference is invisible against the label-collision tolerance.
        var total = 0.0;
        for (var i = 0; i + 1 < positions.Count; i++)
        {
            total += SegmentLength(positions[i], positions[i + 1]);
        }

        if (total == 0)
        {
            // All vertices coincide — treat as a point. Without this, the search loop would
            // divide by zero on the first segment.
            return projection.ToPixel(positions[0]);
        }

        var target = total / 2;
        var accum = 0.0;
        // Always returns: the final iteration's `accum >= target` (after summing all segments) is
        // accum == total >= total/2 = target, and the `i + 2 == positions.Count` clause handles
        // floating-point drift where the cumulative sum can fall an ulp short of `total`. So the
        // loop is guaranteed to hit its return on or before the last segment.
        for (var i = 0; ; i++)
        {
            var segment = SegmentLength(positions[i], positions[i + 1]);
            accum += segment;

            if (!(accum >= target) && i + 2 != positions.Count)
            {
                continue;
            }

            // `segment > 0` here: the only path that could reach this return with a zero-length
            // segment is the `accum >= target` branch fired on a zero-length segment after
            // accum was already < target — impossible, since a zero-length segment doesn't
            // change accum. The clamp covers the FP-drift fall-through where the computed t
            // could land an ulp outside [0, 1].
            var t = Math.Clamp((target - (accum - segment)) / segment, 0, 1);
            var from = positions[i];
            var to = positions[i + 1];
            return projection.ToPixel(new(from.X + t * (to.X - from.X), from.Y + t * (to.Y - from.Y)));
        }
    }

    static (double X, double Y)? LongestLineAnchor(IReadOnlyList<LineString> lines, Projection projection)
    {
        LineString? longest = null;
        var longestLength = -1.0;
        foreach (var line in lines)
        {
            var length = 0.0;
            for (var i = 0; i + 1 < line.Positions.Count; i++)
            {
                length += SegmentLength(line.Positions[i], line.Positions[i + 1]);
            }

            if (length > longestLength)
            {
                longestLength = length;
                longest = line;
            }
        }

        return longest == null ? null : LineAnchor(longest.Positions, projection);
    }

    static (double X, double Y)? PolygonAnchor(IReadOnlyList<Position> ring, Projection projection)
    {
        if (ring.Count < 3)
        {
            return null;
        }

        // Signed-area-weighted centroid (the standard shoelace centroid). Handles closed rings
        // (first == last) and unclosed equally because the duplicate-vertex edge contributes a
        // zero cross product. For a self-intersecting or zero-area ring the formula collapses;
        // fall back to the arithmetic mean of vertices so we still emit *some* anchor — placing
        // the label even slightly off is better than dropping it silently for a malformed input.
        double cx = 0;
        double cy = 0;
        double areaSum = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            areaSum += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }

        if (Math.Abs(areaSum) < 1e-12)
        {
            double sumX = 0;
            double sumY = 0;
            foreach (var position in ring)
            {
                sumX += position.X;
                sumY += position.Y;
            }

            return projection.ToPixel(new(sumX / ring.Count, sumY / ring.Count));
        }

        return projection.ToPixel(new(cx / (3 * areaSum), cy / (3 * areaSum)));
    }

    static (double X, double Y)? LargestPolygonAnchor(IReadOnlyList<Polygon> polygons, Projection projection)
    {
        Polygon? largest = null;
        var largestArea = -1.0;
        foreach (var polygon in polygons)
        {
            if (polygon.Rings.Count == 0)
            {
                continue;
            }

            // Absolute signed area — orientation only signals winding, not size.
            var area = Math.Abs(Ring.SignedArea(polygon.Rings[0]));
            if (area > largestArea)
            {
                largestArea = area;
                largest = polygon;
            }
        }

        if (largest == null)
        {
            return null;
        }

        return PolygonAnchor(largest.Rings[0], projection);
    }

    static double SegmentLength(Position a, Position b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
