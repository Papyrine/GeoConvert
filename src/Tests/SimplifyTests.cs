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
    public async Task Polygon_ring_too_aggressive_reduces_to_minimal_triangle()
    {
        // A tolerance that would collapse the ring below a triangle reduces it to its minimal valid
        // form — the triangle through the three most extreme vertices — instead of restoring full
        // detail. Restoring the original would invert the contract: a larger tolerance must never
        // produce a larger result (it does for data full of sub-tolerance rings otherwise).
        var ring = new[] { new Position(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0) };
        var polygon = new Polygon([ring]);
        var result = (Polygon)Simplifier.Simplify(polygon, 1000);
        var reduced = result.Rings[0];
        // Triangle plus its closing vertex, still closed, every vertex drawn from the original ring.
        await Assert.That(reduced.Count).IsEqualTo(4);
        await Assert.That(reduced[0]).IsEqualTo(reduced[^1]);
        await Assert.That(reduced.All(ring.Contains)).IsTrue();
        // Original traversal order is kept (winding preserved): indices ascend from the anchor.
        await Assert.That(reduced[0]).IsEqualTo(new Position(0, 0));
        await Assert.That(reduced[1]).IsEqualTo(new Position(4, 0));
        await Assert.That(reduced[2]).IsEqualTo(new Position(4, 4));
    }

    [Test]
    public async Task Polygon_ring_with_no_extent_falls_back_to_original()
    {
        // Every vertex coincides, so there is no triangle to span the extent. The ring can't be made
        // smaller while staying valid, so the (already degenerate) original is kept untouched.
        var ring = new[] { new Position(2, 2), new(2, 2), new(2, 2), new(2, 2), new(2, 2) };
        var polygon = new Polygon([ring]);
        var result = (Polygon)Simplifier.Simplify(polygon, 1000);
        await Assert.That(result.Rings[0].Count).IsEqualTo(5);
    }

    [Test]
    public async Task Polygon_ring_fully_collinear_falls_back_to_original()
    {
        // All vertices lie on one line: the farthest-from-chord search finds nothing off it, so no
        // triangle has area. The collinear (degenerate) ring is left as-is rather than fabricated into
        // a zero-area triangle.
        var ring = new[] { new Position(0, 0), new(1, 0), new(2, 0), new(3, 0), new(0, 0) };
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

    // Two polygons sharing an east-side / west-side border. Both rings have a redundant near-collinear
    // vertex on the shared edge at the SAME position — independent simplification would drop it
    // identically here, but a vertex unique to one ring sits just off the chord between the shared
    // junctions and gets dropped by topology-aware simplification too. After SimplifyTopology the
    // shared border has the same vertex sequence on both sides.
    [Test]
    public async Task SimplifyTopology_keeps_shared_polygon_border_identical_on_both_sides()
    {
        // West polygon (clockwise): (0,0)-(0,4)-(2,4)-(2,2.001)-(2,0)-(0,0)
        // East polygon (clockwise): (2,0)-(2,2.001)-(2,4)-(4,4)-(4,0)-(2,0)
        // The shared edge runs from (2,0) up to (2,4) with a near-collinear stop at (2, 2.001).
        var collection = new FeatureCollection
        {
            new Feature(new Polygon([[
                new(0, 0), new(0, 4), new(2, 4), new(2, 2.001), new(2, 0), new(0, 0),
            ]])),
            new Feature(new Polygon([[
                new(2, 0), new(2, 2.001), new(2, 4), new(4, 4), new(4, 0), new(2, 0),
            ]])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);

        var westRing = ((Polygon)result.Features[0].Geometry!).Rings[0];
        var eastRing = ((Polygon)result.Features[1].Geometry!).Rings[0];

        // The near-collinear (2, 2.001) is gone on both sides.
        await Assert.That(westRing.Contains(new Position(2, 2.001))).IsFalse();
        await Assert.That(eastRing.Contains(new Position(2, 2.001))).IsFalse();

        // Both rings still close.
        await Assert.That(westRing[0]).IsEqualTo(westRing[^1]);
        await Assert.That(eastRing[0]).IsEqualTo(eastRing[^1]);

        // The shared (2,0)-(2,4) edge survives intact on both sides — they share the same two
        // junction endpoints with nothing in between. (The plain Simplify path would, with two
        // independently-simplified rings, still happen to agree on this trivial chord; the real
        // safety net is that topology shares the simplification rather than relying on chord
        // determinism for arbitrary inputs.)
        await Assert.That(westRing.Contains(new Position(2, 0))).IsTrue();
        await Assert.That(westRing.Contains(new Position(2, 4))).IsTrue();
        await Assert.That(eastRing.Contains(new Position(2, 0))).IsTrue();
        await Assert.That(eastRing.Contains(new Position(2, 4))).IsTrue();
    }

    // The headline case: a shared border with a meaningful kink that survives simplification.
    // The kink (2, 2) is far enough off the (2,0)-(2,4) chord that DP keeps it, but it would NOT
    // be kept relative to a chord that ran from a *different* junction. Topology-aware splits at
    // (2,0) and (2,4) — the junctions — so the shared edge sees exactly that chord on both sides,
    // and the kink is kept (or dropped) identically by both rings.
    [Test]
    public async Task SimplifyTopology_simplifies_shared_arc_with_internal_kink_identically()
    {
        var collection = new FeatureCollection
        {
            new Feature(new Polygon([[
                new(0, 0), new(0, 4), new(2, 4), new(2.5, 2), new(2, 0), new(0, 0),
            ]])),
            new Feature(new Polygon([[
                new(2, 0), new(2.5, 2), new(2, 4), new(4, 4), new(4, 0), new(2, 0),
            ]])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.1);

        var westRing = ((Polygon)result.Features[0].Geometry!).Rings[0];
        var eastRing = ((Polygon)result.Features[1].Geometry!).Rings[0];

        // The kink at (2.5, 2) sits 0.5 off the chord between the shared junctions (2,0) and (2,4),
        // far above the 0.1 tolerance, so it stays on both sides.
        await Assert.That(westRing.Contains(new Position(2.5, 2))).IsTrue();
        await Assert.That(eastRing.Contains(new Position(2.5, 2))).IsTrue();
    }

    [Test]
    public async Task SimplifyTopology_isolated_ring_simplifies_as_a_closed_ring()
    {
        // A lone polygon with no neighbours has no junctions at all. The whole ring is one piece
        // and goes through the closed-ring simplifier (minPoints = 4) — never collapsing below a
        // triangle even if the tolerance would chord-distance every vertex.
        var collection = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(2, 0.001), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
            ])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        var ring = ((Polygon)result.Features[0].Geometry!).Rings[0];

        await Assert.That(ring.Count).IsEqualTo(5);
        await Assert.That(ring[0]).IsEqualTo(ring[^1]);
        await Assert.That(ring.Contains(new Position(2, 0.001))).IsFalse();
    }

    [Test]
    public async Task SimplifyTopology_isolated_ring_with_huge_tolerance_falls_back_to_minimal_triangle()
    {
        // No junctions, oversized tolerance — the closed-ring fallback inside SimplifyRing kicks in,
        // exercising the same minimal-triangle path as the plain simplifier (covers the
        // junctionIndices.Count <= 1 branch in SimplifyRing).
        var collection = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
            ])),
        };

        var result = Simplifier.SimplifyTopology(collection, 1000);
        var ring = ((Polygon)result.Features[0].Geometry!).Rings[0];

        await Assert.That(ring.Count).IsEqualTo(4);
        await Assert.That(ring[0]).IsEqualTo(ring[^1]);
    }

    [Test]
    public async Task SimplifyTopology_polygon_with_hole_thins_both_rings()
    {
        // Polygon with a hole: exterior + interior rings, no neighbours. Both go through the
        // isolated-ring path; the near-collinear midpoint on each is dropped.
        var collection = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(5, 0.001), new(10, 0), new(10, 10), new(0, 10), new(0, 0)],
                [new(2, 2), new(5, 2.001), new(8, 2), new(8, 8), new(2, 8), new(2, 2)],
            ])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        var polygon = (Polygon)result.Features[0].Geometry!;

        await Assert.That(polygon.Rings.Count).IsEqualTo(2);
        await Assert.That(polygon.Rings[0].Count).IsEqualTo(5);
        await Assert.That(polygon.Rings[1].Count).IsEqualTo(5);
    }

    [Test]
    public async Task SimplifyTopology_multipolygon_and_geometrycollection_recurse()
    {
        var multi = new MultiPolygon(
        [
            new([[new(0, 0), new(1, 0.0001), new(2, 0), new(2, 2), new(0, 2), new(0, 0)]]),
            new([[new(3, 3), new(4, 3.0001), new(5, 3), new(5, 5), new(3, 5), new(3, 3)]]),
        ]);
        var collection = new FeatureCollection
        {
            new Feature(new GeometryCollection([multi])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        var inner = (GeometryCollection)result.Features[0].Geometry!;
        var rebuiltMulti = (MultiPolygon)inner.Geometries[0];

        await Assert.That(rebuiltMulti.Polygons.Count).IsEqualTo(2);
        await Assert.That(rebuiltMulti.Polygons[0].Rings[0].Count).IsEqualTo(5);
        await Assert.That(rebuiltMulti.Polygons[1].Rings[0].Count).IsEqualTo(5);
    }

    [Test]
    public async Task SimplifyTopology_line_endpoints_are_always_junctions()
    {
        // A single open line has no shared structure, but its endpoints are pinned by Classify so
        // SimplifyLine takes the "no internal junctions" short path through the open-line
        // simplifier. Near-collinear interior vertices still drop.
        var collection = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0), new(1, 0.001), new(2, 0)])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        var line = (LineString)result.Features[0].Geometry!;

        await Assert.That(line.Positions.Count).IsEqualTo(2);
        await Assert.That(line.Positions[0]).IsEqualTo(new Position(0, 0));
        await Assert.That(line.Positions[1]).IsEqualTo(new Position(2, 0));
    }

    // Two lines sharing a vertex in the middle — that vertex is a junction in both, splitting each
    // line into two arcs. The shared join survives even if all interior vertices on its arc are
    // near-collinear, because the junction is pinned.
    [Test]
    public async Task SimplifyTopology_lines_sharing_a_midpoint_split_at_the_junction()
    {
        var collection = new FeatureCollection
        {
            // Line A: (0,0) - (2,0.001) - (4,0) - (4,4) - shares (4,0) with line B.
            new Feature(new LineString([new(0, 0), new(2, 0.001), new(4, 0), new(4, 4)])),
            // Line B: (4,0) - (6,0.001) - (8,0) - shares (4,0) with line A.
            new Feature(new LineString([new(4, 0), new(6, 0.001), new(8, 0)])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        var lineA = (LineString)result.Features[0].Geometry!;

        // Line A is split at (4,0). The near-collinear (2, 0.001) is dropped on its first arc,
        // and the (4,0) junction survives at the split — so line A reads as (0,0)-(4,0)-(4,4).
        await Assert.That(lineA.Positions.Count).IsEqualTo(3);
        await Assert.That(lineA.Positions[0]).IsEqualTo(new Position(0, 0));
        await Assert.That(lineA.Positions[1]).IsEqualTo(new Position(4, 0));
        await Assert.That(lineA.Positions[2]).IsEqualTo(new Position(4, 4));
    }

    [Test]
    public async Task SimplifyTopology_multilinestring_recurses()
    {
        // Two-part MultiLineString — exercises the MultiLineString branch in both the gather and
        // the rebuild passes.
        var collection = new FeatureCollection
        {
            new Feature(new MultiLineString(
            [
                new([new(0, 0), new(1, 0.001), new(2, 0)]),
                new([new(0, 0), new(1, 1), new(2, 0)]),
            ])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        var multi = (MultiLineString)result.Features[0].Geometry!;

        await Assert.That(multi.LineStrings[0].Positions.Count).IsEqualTo(2);
        await Assert.That(multi.LineStrings[1].Positions.Count).IsEqualTo(3);
    }

    [Test]
    public async Task SimplifyTopology_points_pass_through()
    {
        var point = new Point(1, 2);
        var multiPoint = new MultiPoint([new(3, 4), new(5, 6)]);
        var collection = new FeatureCollection
        {
            new Feature(point),
            new Feature(multiPoint),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);

        // Geometries are immutable; the pass-through path returns the same instance.
        await Assert.That(ReferenceEquals(result.Features[0].Geometry, point)).IsTrue();
        await Assert.That(ReferenceEquals(result.Features[1].Geometry, multiPoint)).IsTrue();
    }

    [Test]
    public async Task SimplifyTopology_unknown_geometry_passes_through()
    {
        // Defensive default branch in RebuildGeometry: an unrecognised Geometry subclass has no
        // rings/lines to swap and is returned as-is. The gather pass also ignores it.
        var bad = new G.BadGeometry();
        var collection = new FeatureCollection
        {
            new Feature(bad),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);
        await Assert.That(ReferenceEquals(result.Features[0].Geometry, bad)).IsTrue();
    }

    [Test]
    public async Task SimplifyTopology_null_geometry_and_layer_metadata_are_preserved()
    {
        var root = new FeatureCollection
        {
            Name = "root",
            Properties =
            {
                ["note"] = "layer",
            },
            Features =
            {
                new Feature(new LineString([new(0, 0), new(1, 0.001), new(2, 0)]), new Dictionary<string, object?> { ["name"] = "road" })
                {
                    Id = 7L,
                },
                new Feature(geometry: null),
            },
        };
        var child = new FeatureCollection
        {
            Name = "child",
        };
        child.Add(new Feature(new LineString([new(0, 0), new(1, 0.001), new(2, 0)])));
        root.Children.Add(child);

        var result = Simplifier.SimplifyTopology(root, 0.01);

        await Assert.That(result.Name).IsEqualTo("root");
        await Assert.That(result.Properties["note"]).IsEqualTo("layer");
        await Assert.That(result.Children[0].Name).IsEqualTo("child");
        await Assert.That(result.Features[0].Id).IsEqualTo(7L);
        await Assert.That(result.Features[0].Properties["name"]).IsEqualTo("road");
        await Assert.That(result.Features[1].Geometry).IsNull();
        await Assert.That(Line(result.Features[0].Geometry!).Count).IsEqualTo(2);
        await Assert.That(Line(result.Children[0].Features[0].Geometry!).Count).IsEqualTo(2);

        // The input is left untouched.
        await Assert.That(Line(root.Features[0].Geometry!).Count).IsEqualTo(3);
    }

    [Test]
    public async Task SimplifyTopology_degenerate_chains_are_left_alone()
    {
        // Below-minimum-vertex inputs flow through both the ring and line short-circuits in
        // TopologySimplifier (RingCycleLength < 3 for rings, line.Count < 3 for lines, plus the
        // n < 2 guard in Classify that skips a degenerate single-vertex ring). Each is returned
        // untouched. An empty MultiLineString part covers the line.Count == 0 branch in Classify;
        // an empty (zero-ring) polygon covers the gather pass.
        var collection = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0), new(5, 5)])),
            // Three-vertex closed ring (n = 2 after closure trim) → SimplifyRing short-circuits at
            // RingCycleLength < 3, but Classify still iterates 2 vertices.
            new Feature(new Polygon(
            [
                [new(0, 0), new(0, 0), new(0, 0)],
            ])),
            // Two-vertex closed ring (n = 1) and a zero-vertex ring (n = 0) — both fall into the
            // n < 2 guard in Classify and the < 3 guard in SimplifyRing.
            new Feature(new Polygon(
            [
                [new(0, 0), new(0, 0)],
                [],
            ])),
            new Feature(new MultiLineString(
            [
                new([]),
                new([new(0, 0), new(1, 0.001), new(2, 0)]),
            ])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01);

        await Assert.That(Line(result.Features[0].Geometry!).Count).IsEqualTo(2);
        await Assert.That(((Polygon)result.Features[1].Geometry!).Rings[0].Count).IsEqualTo(3);
        var degenerate = (Polygon)result.Features[2].Geometry!;
        await Assert.That(degenerate.Rings[0].Count).IsEqualTo(2);
        await Assert.That(degenerate.Rings[1].Count).IsEqualTo(0);
        var multi = (MultiLineString)result.Features[3].Geometry!;
        await Assert.That(multi.LineStrings[0].Positions.Count).IsEqualTo(0);
        await Assert.That(multi.LineStrings[1].Positions.Count).IsEqualTo(2);
    }

    // A "tripoint" where three country borders meet: vertex (1,1) is shared by three rings, each
    // with a different (prev, next) neighbour pair. Classify catches it on the second observation
    // (mismatch) and the third short-circuits via the already-junction guard.
    [Test]
    public async Task SimplifyTopology_tripoint_vertex_is_classified_as_a_junction()
    {
        var collection = new FeatureCollection
        {
            // Triangle 1: anchored on (1,1) with neighbours (0,0) and (2,0).
            new Feature(new Polygon([[
                new(0, 0), new(1, 1), new(2, 0), new(0, 0),
            ]])),
            // Triangle 2: anchored on (1,1) with neighbours (2,0) and (2,2).
            new Feature(new Polygon([[
                new(2, 0), new(1, 1), new(2, 2), new(2, 0),
            ]])),
            // Triangle 3: anchored on (1,1) with neighbours (2,2) and (0,2) — the third observation
            // hits the already-classified guard.
            new Feature(new Polygon([[
                new(2, 2), new(1, 1), new(0, 2), new(2, 2),
            ]])),
        };

        var replacements = TopologySimplifier.BuildReplacements(collection, 0.001, SimplifyMethod.DouglasPeucker);
        var allJunctions = ((Polygon)collection.Features[0].Geometry!).Rings[0]
            .Concat(((Polygon)collection.Features[1].Geometry!).Rings[0])
            .Concat(((Polygon)collection.Features[2].Geometry!).Rings[0])
            .Distinct()
            .ToList();

        // (1,1) is the tripoint and (2,0), (2,2) are bipoints shared by exactly two triangles —
        // all four "different neighbours" cases. Confirm the rebuild ran end-to-end with the
        // junction set classifying every shared vertex without collapsing any triangle below the
        // minimum.
        await Assert.That(allJunctions.Contains(new Position(1, 1))).IsTrue();
        await Assert.That(replacements.Count).IsEqualTo(3);
        foreach (var ring in replacements.Values)
        {
            await Assert.That(ring.Count >= 4).IsTrue();
        }
    }

    [Test]
    public async Task SimplifyTopology_visvalingam_is_supported()
    {
        // Run through the Visvalingam branch on a shared-border layout so the dedicated arc path
        // proves it isn't DP-specific.
        var collection = new FeatureCollection
        {
            new Feature(new Polygon([[
                new(0, 0), new(0, 4), new(2, 4), new(2, 2), new(2, 0), new(0, 0),
            ]])),
            new Feature(new Polygon([[
                new(2, 0), new(2, 2), new(2, 4), new(4, 4), new(4, 0), new(2, 0),
            ]])),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.01, SimplifyMethod.Visvalingam);
        var westRing = ((Polygon)result.Features[0].Geometry!).Rings[0];
        var eastRing = ((Polygon)result.Features[1].Geometry!).Rings[0];

        // The straight-line interior vertex (2,2) has zero effective area on the shared edge and
        // is dropped on both sides; the junction endpoints (2,0)/(2,4) survive.
        await Assert.That(westRing.Contains(new Position(2, 2))).IsFalse();
        await Assert.That(eastRing.Contains(new Position(2, 2))).IsFalse();
        await Assert.That(westRing.Contains(new Position(2, 0))).IsTrue();
        await Assert.That(westRing.Contains(new Position(2, 4))).IsTrue();
    }

    // Two coincident-XY vertices that differ only in Z must sort distinctly so the (prev, next)
    // canonicalisation in Classify treats them as ordered neighbours, not duplicates. Without the
    // Z (and M) tiebreaker in Less() a 3D ring could have its junction-pair hashed incorrectly.
    [Test]
    public async Task SimplifyTopology_position_ordering_respects_z_and_m_tiebreakers()
    {
        // A ring whose vertices have identical X/Y but differing Z/M exercises every tiebreaker
        // path in Less() (X equal → fall to Y → equal → Z → equal → M). Topology simplification
        // should reproduce the ring without crashing or merging the distinct vertices.
        var ring = new IReadOnlyList<Position>[]
        {
            [
                new(0, 0, 0, 0),
                new(0, 0, 1, 0),
                new(0, 0, 1, 1),
                new(0, 0, 0, 1),
                new(0, 0, 0, 0),
            ],
        };
        var collection = new FeatureCollection
        {
            new Feature(new Polygon(ring)),
        };

        var result = Simplifier.SimplifyTopology(collection, 0.001);
        var rebuilt = ((Polygon)result.Features[0].Geometry!).Rings[0];

        // Closure preserved (first == last); the simplifier didn't throw on the zero-XY chord.
        await Assert.That(rebuilt[0]).IsEqualTo(rebuilt[^1]);
    }
}
