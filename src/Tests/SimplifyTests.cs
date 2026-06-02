using G = TestSupport;

// Exercises Simplifier (the public facade) and LineSimplifier (the Douglas–Peucker /
// Visvalingam–Whyatt vertex reduction behind it).
public class SimplifyTests
{
    static IReadOnlyList<Position> Line(Geometry geometry) =>
        ((LineString)geometry).Positions;

    [Test]
    public async Task DouglasPeucker_drops_collinear_vertices()
    {
        var line = new LineString([new(0, 0), new(1, 0), new(2, 0)]);
        var result = Line(Simplifier.Simplify(line, 0.0001));
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsEqualTo(new Position(0, 0));
        await Assert.That(result[1]).IsEqualTo(new Position(2, 0));
    }

    [Test]
    public async Task DouglasPeucker_keeps_significant_vertex()
    {
        // The apex sits 1 unit off the (0,0)-(2,0) chord — above the 0.5 tolerance, so it stays.
        var line = new LineString([new(0, 0), new(1, 1), new(2, 0)]);
        var result = Line(Simplifier.Simplify(line, 0.5));
        await Assert.That(result.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DouglasPeucker_keeps_endpoints_and_preserves_z()
    {
        var line = new LineString([new(0, 0, 10), new(1, 0.001, 20), new(2, 0, 30)]);
        var result = Line(Simplifier.Simplify(line, 0.01));
        await Assert.That(result.Count).IsEqualTo(2);
        // Surviving vertices keep their elevation — simplification removes points, never interpolates.
        await Assert.That(result[0].Z).IsEqualTo(10d);
        await Assert.That(result[1].Z).IsEqualTo(30d);
    }

    [Test]
    public async Task Visvalingam_removes_low_area_vertices_then_halts_at_threshold()
    {
        // Indices 1 and 5 are near-collinear spikes (effective area ~0.001); index 3 is a real
        // apex (area 3). With threshold 0.1 the two spikes go and the apex (and its neighbours,
        // re-areaed to 3) survive — exercising the lazy-deletion skip, the threshold break, and
        // both arms of Rearea (the endpoint-adjacent removal pins index 0).
        var line = new LineString(
        [
            new(0, 0),
            new(1, 0.001),
            new(2, 0),
            new(3, 3),
            new(4, 0),
            new(5, 0.001),
            new(6, 0),
        ]);
        var result = Line(Simplifier.Simplify(line, 0.1, SimplifyMethod.Visvalingam));
        await Assert.That(result.Count).IsEqualTo(5);
        await Assert.That(result.Contains(new Position(3, 3))).IsTrue();
        await Assert.That(result.Contains(new Position(1, 0.001))).IsFalse();
    }

    [Test]
    public async Task Visvalingam_collapses_collinear_line_to_endpoints()
    {
        // Every interior vertex has zero effective area, all below the threshold, so removal runs
        // until only the two endpoints remain (the minPoints floor exits the loop).
        var line = new LineString([new(0, 0), new(1, 0), new(2, 0), new(3, 0)]);
        var result = Line(Simplifier.Simplify(line, 1, SimplifyMethod.Visvalingam));
        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Below_minimum_vertex_count_is_unchanged()
    {
        var line = new LineString([new(0, 0), new(5, 5)]);
        await Assert.That(Line(Simplifier.Simplify(line, 100)).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Polygon_ring_simplified_while_keeping_closure_and_validity()
    {
        // A square with a redundant near-collinear vertex on the bottom edge.
        var polygon = new Polygon(
        [
            [new(0, 0), new(2, 0.001), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
        ]);
        var result = (Polygon)Simplifier.Simplify(polygon, 0.01);
        var ring = result.Rings[0];
        await Assert.That(ring.Count).IsEqualTo(5);
        // First and last vertex still coincide — the ring stays closed.
        await Assert.That(ring[0]).IsEqualTo(ring[^1]);
        await Assert.That(ring.Contains(new Position(2, 0.001))).IsFalse();
    }

    [Test]
    public async Task Polygon_ring_too_aggressive_falls_back_to_original()
    {
        // A tolerance that would collapse the ring below a triangle leaves it untouched, so the
        // polygon stays valid rather than degenerating.
        var ring = new[] { new Position(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0) };
        var polygon = new Polygon([ring]);
        var result = (Polygon)Simplifier.Simplify(polygon, 1000);
        await Assert.That(result.Rings[0].Count).IsEqualTo(5);
    }

    [Test]
    public async Task Point_and_multipoint_pass_through_unchanged()
    {
        var point = new Point(1, 2);
        await Assert.That(ReferenceEquals(Simplifier.Simplify(point, 0.1), point)).IsTrue();

        var multiPoint = new MultiPoint([new(0, 0), new(1, 1), new(2, 2)]);
        await Assert.That(ReferenceEquals(Simplifier.Simplify(multiPoint, 0.1), multiPoint)).IsTrue();
    }

    [Test]
    public async Task Unknown_geometry_passes_through()
    {
        var geometry = new G.BadGeometry();
        await Assert.That(ReferenceEquals(Simplifier.Simplify(geometry, 0.1), geometry)).IsTrue();
    }

    [Test]
    public async Task MultiLineString_simplifies_each_part()
    {
        var multi = new MultiLineString(
        [
            new([new(0, 0), new(1, 0), new(2, 0)]),
            new([new(0, 0), new(1, 1), new(2, 0)]),
        ]);
        var result = (MultiLineString)Simplifier.Simplify(multi, 0.5);
        await Assert.That(result.LineStrings[0].Positions.Count).IsEqualTo(2);
        await Assert.That(result.LineStrings[1].Positions.Count).IsEqualTo(3);
    }

    [Test]
    public async Task MultiPolygon_simplifies_each_polygon()
    {
        var multi = new MultiPolygon(
        [
            new([[new(0, 0), new(2, 0.001), new(4, 0), new(4, 4), new(0, 4), new(0, 0)]]),
        ]);
        var result = (MultiPolygon)Simplifier.Simplify(multi, 0.01);
        await Assert.That(result.Polygons[0].Rings[0].Count).IsEqualTo(5);
    }

    [Test]
    public async Task GeometryCollection_recurses()
    {
        var collection = new GeometryCollection(
        [
            new Point(1, 2),
            new LineString([new(0, 0), new(1, 0), new(2, 0)]),
        ]);
        var result = (GeometryCollection)Simplifier.Simplify(collection, 0.0001);
        await Assert.That(result.Geometries[0].Type).IsEqualTo(GeometryType.Point);
        await Assert.That(((LineString)result.Geometries[1]).Positions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Unknown_method_throws()
    {
        var line = new LineString([new(0, 0), new(1, 0), new(2, 0)]);
        await Assert.That(G.ThrowsGeo(() => Simplifier.Simplify(line, 0.1, (SimplifyMethod)99))).IsTrue();
    }

    [Test]
    public async Task FeatureCollection_tree_is_copied_and_simplified()
    {
        var root = new FeatureCollection
        {
            Name = "root",
            Properties =
            {
                ["note"] = "layer"
            },
            Features =
            {
                new Feature(new LineString([new(0, 0), new(1, 0), new(2, 0)]), new Dictionary<string, object?> { ["name"] = "road" })
                {
                    Id = 7L
                },
                new Feature(geometry: null)
            }
        };
        var child = new FeatureCollection
        {
            Name = "child"
        };
        child.Add(new Feature(new LineString([new(0, 0), new(1, 0), new(2, 0)])));
        root.Children.Add(child);

        var result = Simplifier.Simplify(root, 0.0001);

        // Structure, names, layer properties, feature id/properties all carried over.
        await Assert.That(result.Name).IsEqualTo("root");
        await Assert.That(result.Properties["note"]).IsEqualTo("layer");
        await Assert.That(result.Children[0].Name).IsEqualTo("child");
        await Assert.That(result.Features[0].Id).IsEqualTo(7L);
        await Assert.That(result.Features[0].Properties["name"]).IsEqualTo("road");
        await Assert.That(result.Features[1].Geometry).IsNull();

        // The line was simplified in both the root and the child layer.
        await Assert.That(Line(result.Features[0].Geometry!).Count).IsEqualTo(2);
        await Assert.That(Line(result.Children[0].Features[0].Geometry!).Count).IsEqualTo(2);

        // The original tree is untouched — Simplify returns a new graph.
        await Assert.That(Line(root.Features[0].Geometry!).Count).IsEqualTo(3);
    }
}
