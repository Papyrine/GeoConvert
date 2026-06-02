namespace GeoConvert;

/// <summary>
/// Lossy vertex reduction (line generalisation) for the in-memory model — drops coordinates that
/// don't change a shape beyond a tolerance, the highest-leverage way to shrink dense vector data
/// before encoding it. Every overload returns a <b>new</b> object graph; the input is left untouched.
/// <para>
/// Point and multi-point geometries pass through unchanged (there's nothing to thin). Line vertices
/// are reduced with the chosen <see cref="SimplifyMethod"/>; polygon rings are reduced per ring while
/// preserving closure and never collapsing below a triangle, so the result stays a valid polygon.
/// Z/M ordinates ride along on the vertices that survive — the algorithms only ever <i>remove</i>
/// whole positions, never interpolate, so no elevation or measure value is invented. Simplification is
/// done in raw lon/lat space (planar), which is the standard, fast choice for WGS84 data at the scales
/// this is useful for.
/// </para>
/// </summary>
public static class Simplifier
{
    /// <summary>
    /// Simplifies every feature in <paramref name="collection"/> and all of its child layers, returning
    /// a new tree that mirrors the original's names, layer-level properties and structure.
    /// </summary>
    public static FeatureCollection Simplify(FeatureCollection collection, double tolerance, SimplifyMethod method = SimplifyMethod.DouglasPeucker)
    {
        var result = new FeatureCollection
        {
            Name = collection.Name,
        };

        foreach (var pair in collection.Properties)
        {
            result.Properties[pair.Key] = pair.Value;
        }

        foreach (var feature in collection.Features)
        {
            result.Features.Add(Simplify(feature, tolerance, method));
        }

        foreach (var child in collection.Children)
        {
            result.Children.Add(Simplify(child, tolerance, method));
        }

        return result;
    }

    /// <summary>
    /// Simplifies a single feature's geometry, returning a new <see cref="Feature"/> that copies the
    /// original's <see cref="Feature.Id"/> and properties. A feature with no geometry is copied as-is.
    /// </summary>
    public static Feature Simplify(Feature feature, double tolerance, SimplifyMethod method = SimplifyMethod.DouglasPeucker)
    {
        Geometry? geometry;
        if (feature.Geometry is { } source)
        {
            geometry = Simplify(source, tolerance, method);
        }
        else
        {
            geometry = null;
        }

        var result = new Feature(geometry)
        {
            Id = feature.Id,
        };

        foreach (var pair in feature.Properties)
        {
            result.Properties[pair.Key] = pair.Value;
        }

        return result;
    }

    /// <summary>
    /// Simplifies a geometry. The meaning of <paramref name="tolerance"/> depends on
    /// <paramref name="method"/>: a perpendicular <b>distance</b> in coordinate units for
    /// <see cref="SimplifyMethod.DouglasPeucker"/>, an effective triangle <b>area</b> in squared
    /// coordinate units for <see cref="SimplifyMethod.Visvalingam"/>. Larger values remove more
    /// vertices. The geometry type is always preserved.
    /// </summary>
    public static Geometry Simplify(Geometry geometry, double tolerance, SimplifyMethod method = SimplifyMethod.DouglasPeucker) =>
        geometry switch
        {
            LineString line => new LineString(SimplifyLine(line.Positions, tolerance, method)),
            Polygon polygon => SimplifyPolygon(polygon, tolerance, method),
            MultiLineString multiLine =>
                new MultiLineString(
                [
                    .. multiLine.LineStrings
                        .Select(_ => new LineString(SimplifyLine(_.Positions, tolerance, method)))
                ]),
            MultiPolygon multiPolygon =>
                new MultiPolygon(
                [
                    .. multiPolygon.Polygons
                        .Select(_ => SimplifyPolygon(_, tolerance, method))
                ]),
            GeometryCollection collection =>
                new GeometryCollection(
                [
                    .. collection.Geometries
                        .Select(_ => Simplify(_, tolerance, method))
                ]),
            // Point, MultiPoint (and any future vertex-less geometry): nothing to thin. Returning the
            // same instance is safe — geometries are immutable — and avoids a needless copy.
            _ => geometry,
        };

    /// <summary>
    /// Topology-preserving variant of <see cref="Simplify(FeatureCollection, double, SimplifyMethod)"/>.
    /// Per-feature <see cref="Simplify(FeatureCollection, double, SimplifyMethod)"/> reduces every ring
    /// independently — so two adjacent polygons that share a border get that border simplified by
    /// different chord choices, and the results no longer line up, leaving hairline gaps (or
    /// alpha-stacking overlaps when the fill is translucent) along every shared edge. This overload
    /// avoids that: it classifies <em>junctions</em> (vertices whose neighbours differ across chains)
    /// across the whole collection tree, splits each chain at its junctions into arcs, and runs the
    /// chosen algorithm on each arc once. Two rings that share an arc see bit-identical simplified
    /// vertices for that arc — adjacent country/state polygons stay seamlessly joined.
    /// <para>
    /// Same usage and tolerance units as the plain overload; the cost is one extra pass over every
    /// vertex (junction analysis) plus the dictionary that drives the rebuild — typically a small
    /// fraction of the simplification time itself on real datasets. Points and multi-points pass
    /// through unchanged. Pick this overload for topologically consistent datasets (e.g. Natural
    /// Earth admin layers) where the per-feature variant's gaps/overlaps would show up at low
    /// stroke widths or with translucent fills; the plain overload is fine when each feature stands
    /// on its own and shared boundaries aren't a concern.
    /// </para>
    /// </summary>
    public static FeatureCollection SimplifyTopology(FeatureCollection collection, double tolerance, SimplifyMethod method = SimplifyMethod.DouglasPeucker)
    {
        var replacements = TopologySimplifier.BuildReplacements(collection, tolerance, method);
        return RebuildCollection(collection, replacements);
    }

    static FeatureCollection RebuildCollection(
        FeatureCollection source,
        Dictionary<IReadOnlyList<Position>, IReadOnlyList<Position>> replacements)
    {
        var result = new FeatureCollection
        {
            Name = source.Name,
        };

        foreach (var pair in source.Properties)
        {
            result.Properties[pair.Key] = pair.Value;
        }

        foreach (var feature in source.Features)
        {
            var geometry = feature.Geometry is { } source_ ? RebuildGeometry(source_, replacements) : null;
            var rebuilt = new Feature(geometry)
            {
                Id = feature.Id,
            };
            foreach (var pair in feature.Properties)
            {
                rebuilt.Properties[pair.Key] = pair.Value;
            }

            result.Features.Add(rebuilt);
        }

        foreach (var child in source.Children)
        {
            result.Children.Add(RebuildCollection(child, replacements));
        }

        return result;
    }

    static Geometry RebuildGeometry(
        Geometry geometry,
        Dictionary<IReadOnlyList<Position>, IReadOnlyList<Position>> replacements) =>
        geometry switch
        {
            LineString line => new LineString(replacements[line.Positions]),
            MultiLineString multi => new MultiLineString(
            [
                .. multi.LineStrings.Select(_ => new LineString(replacements[_.Positions]))
            ]),
            Polygon polygon => new Polygon(RebuildRings(polygon, replacements)),
            MultiPolygon multi => new MultiPolygon(
            [
                .. multi.Polygons.Select(_ => new Polygon(RebuildRings(_, replacements)))
            ]),
            GeometryCollection collection => new GeometryCollection(
            [
                .. collection.Geometries.Select(_ => RebuildGeometry(_, replacements))
            ]),
            // Point, MultiPoint, anything else: no rings/lines to swap; pass the same instance
            // through. Geometries are immutable so sharing is safe.
            _ => geometry,
        };

    static IReadOnlyList<IReadOnlyList<Position>> RebuildRings(
        Polygon polygon,
        Dictionary<IReadOnlyList<Position>, IReadOnlyList<Position>> replacements)
    {
        var rings = new List<IReadOnlyList<Position>>(polygon.Rings.Count);
        foreach (var ring in polygon.Rings)
        {
            rings.Add(replacements[ring]);
        }

        return rings;
    }

    static Polygon SimplifyPolygon(Polygon polygon, double tolerance, SimplifyMethod method)
    {
        var rings = new List<IReadOnlyList<Position>>(polygon.Rings.Count);
        foreach (var ring in polygon.Rings)
        {
            // minPoints = 4: three distinct corners plus the closing vertex — the smallest valid ring.
            rings.Add(LineSimplifier.Simplify(ring, tolerance, method, 4));
        }

        return new(rings);
    }

    static IReadOnlyList<Position> SimplifyLine(IReadOnlyList<Position> positions, double tolerance, SimplifyMethod method) =>
        LineSimplifier.Simplify(positions, tolerance, method, 2);
}
