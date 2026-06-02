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
        var geometry = feature.Geometry is { } source ? Simplify(source, tolerance, method) : null;
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
            MultiLineString multiLine => new MultiLineString(
                [.. multiLine.LineStrings.Select(_ => new LineString(SimplifyLine(_.Positions, tolerance, method)))]),
            MultiPolygon multiPolygon => new MultiPolygon(
                [.. multiPolygon.Polygons.Select(_ => SimplifyPolygon(_, tolerance, method))]),
            GeometryCollection collection => new GeometryCollection(
                [.. collection.Geometries.Select(_ => Simplify(_, tolerance, method))]),
            // Point, MultiPoint (and any future vertex-less geometry): nothing to thin. Returning the
            // same instance is safe — geometries are immutable — and avoids a needless copy.
            _ => geometry,
        };

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
