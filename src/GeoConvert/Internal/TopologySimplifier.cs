/// <summary>
/// Topology-preserving simplification — the shared-boundary fix behind
/// <see cref="Simplifier.SimplifyTopology"/>. Plain per-feature simplification is independent:
/// two adjacent polygons that share a border get that border simplified <em>twice</em>, by
/// different chord choices, and the simplified versions no longer line up — leaving hairline gaps
/// or alpha-stacking overlaps along every shared edge. This module's contract is the opposite: a
/// boundary edge shared by two rings is reduced to the <b>same</b> sequence of vertices on both
/// sides, so adjacent polygons stay seamlessly joined after thinning.
/// <para>
/// Algorithm (the TopoJSON arc model, minus the dedup-by-content step):
/// <list type="number">
/// <item>Collect every linear chain (polygon ring or polyline) in the tree.</item>
/// <item>Classify <b>junction</b> vertices — positions whose neighbours differ across two
/// chains (or two passes through the same chain). Open-line endpoints are pinned as junctions
/// too. A shared border's vertices have identical neighbours on both sides, so they are
/// <em>not</em> junctions; the shared border's two endpoints (where the borders fan out into
/// different country shapes) <em>are</em>.</item>
/// <item>Split each chain at its junctions into arcs and simplify each arc independently with
/// <see cref="LineSimplifier"/>, pinning the junction endpoints. Douglas–Peucker and
/// Visvalingam are both direction-symmetric — the kept-vertex set only depends on the input
/// sequence as a set with order, not on which end you start from — so the two rings that share
/// an arc (one traversing it forward, one backward) get bit-identical simplified vertices for
/// that arc.</item>
/// <item>Reassemble each chain by concatenating its simplified arcs, skipping the duplicated
/// junction vertex where consecutive arcs meet. A ring whose arcs all collapse to their junctions
/// reassembles below a valid triangle — the per-arc floor of two points can't see the ring it is
/// building — so it falls back to whole-ring simplification, floored at four positions.</item>
/// </list>
/// Per the algorithm: identical input across both occurrences ⇒ identical output ⇒ shared
/// boundary preserved exactly. The dictionary of (original-chain-ref ⇒ simplified-chain) is then
/// used by <see cref="Simplifier"/> to mirror the source tree with simplified geometry.
/// </para>
/// </summary>
static class TopologySimplifier
{
    /// <summary>
    /// Walks <paramref name="collection"/>, runs junction analysis across every ring and line
    /// found, and returns a dictionary keyed on the original chain references that maps each to
    /// its topology-aware simplified replacement. Point geometries contribute nothing (no chain
    /// to thin) and are absent from the dictionary; callers must handle them by reference
    /// pass-through.
    /// </summary>
    public static Dictionary<IReadOnlyList<Position>, IReadOnlyList<Position>> BuildReplacements(
        FeatureCollection collection,
        double tolerance,
        SimplifyMethod method)
    {
        var rings = new List<IReadOnlyList<Position>>();
        var lines = new List<IReadOnlyList<Position>>();
        Gather(collection, rings, lines);

        var junctions = Classify(rings, lines);

        // ReferenceEqualityComparer so two distinct-but-equal Position lists are still simplified
        // independently — only objects we actually saw in the tree get replaced on rebuild.
        var replacements = new Dictionary<IReadOnlyList<Position>, IReadOnlyList<Position>>(ReferenceEqualityComparer.Instance);
        foreach (var ring in rings)
        {
            if (!replacements.ContainsKey(ring))
            {
                replacements[ring] = SimplifyRing(ring, junctions, tolerance, method);
            }
        }

        foreach (var line in lines)
        {
            if (!replacements.ContainsKey(line))
            {
                replacements[line] = SimplifyLine(line, junctions, tolerance, method);
            }
        }

        return replacements;
    }

    static void Gather(FeatureCollection collection, List<IReadOnlyList<Position>> rings, List<IReadOnlyList<Position>> lines)
    {
        foreach (var feature in collection.Features)
        {
            if (feature.Geometry is { } geometry)
            {
                GatherGeometry(geometry, rings, lines);
            }
        }

        foreach (var child in collection.Children)
        {
            Gather(child, rings, lines);
        }
    }

    static void GatherGeometry(Geometry geometry, List<IReadOnlyList<Position>> rings, List<IReadOnlyList<Position>> lines)
    {
        switch (geometry)
        {
            case LineString line:
                lines.Add(line.Positions);
                break;
            case MultiLineString multi:
                foreach (var part in multi.LineStrings)
                {
                    lines.Add(part.Positions);
                }
                break;
            case Polygon polygon:
                foreach (var ring in polygon.Rings)
                {
                    rings.Add(ring);
                }
                break;
            case MultiPolygon multi:
                foreach (var polygon in multi.Polygons)
                {
                    foreach (var ring in polygon.Rings)
                    {
                        rings.Add(ring);
                    }
                }
                break;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    GatherGeometry(child, rings, lines);
                }
                break;
            // Point, MultiPoint, and any unrecognised geometry: no vertex chain to feed into
            // junction analysis. Pass-through on rebuild.
        }
    }

    // Junction := a vertex whose set of (predecessor, successor) neighbour pairs across all
    // occurrences in all chains contains more than one distinct pair. The pair is canonicalised
    // unordered so a chain that traverses the same arc in the opposite direction matches as the
    // same neighbours (and so doesn't spuriously make every shared-arc vertex a junction).
    static HashSet<Position> Classify(List<IReadOnlyList<Position>> rings, List<IReadOnlyList<Position>> lines)
    {
        var junctions = new HashSet<Position>();
        var firstSeen = new Dictionary<Position, (Position Lo, Position Hi)>();

        foreach (var ring in rings)
        {
            var n = RingCycleLength(ring);
            if (n < 2)
            {
                continue;
            }

            for (var i = 0; i < n; i++)
            {
                var prev = ring[(i - 1 + n) % n];
                var curr = ring[i];
                var next = ring[(i + 1) % n];
                Observe(junctions, firstSeen, curr, prev, next);
            }
        }

        foreach (var line in lines)
        {
            if (line.Count == 0)
            {
                continue;
            }

            // Open-line endpoints are always junctions: the "open" end has no successor (or
            // predecessor) and so couldn't be classified by neighbour-pair anyway.
            junctions.Add(line[0]);
            junctions.Add(line[^1]);
            for (var i = 1; i < line.Count - 1; i++)
            {
                Observe(junctions, firstSeen, line[i], line[i - 1], line[i + 1]);
            }
        }

        return junctions;
    }

    // The number of distinct positions a closed ring traverses — the input includes its closing
    // vertex (first == last by polygon invariant), which would otherwise show up as a phantom
    // (prev, next) sample at i=last. Open or malformed rings (no closure) fall back to the raw
    // count, which is correct for them.
    static int RingCycleLength(IReadOnlyList<Position> ring) =>
        ring.Count > 0 && ring[0].Equals(ring[^1])
            ? ring.Count - 1
            : ring.Count;

    static void Observe(
        HashSet<Position> junctions,
        Dictionary<Position, (Position Lo, Position Hi)> firstSeen,
        Position curr,
        Position prev,
        Position next)
    {
        if (junctions.Contains(curr))
        {
            return;
        }

        var pair = Less(prev, next) ? (prev, next) : (next, prev);
        if (firstSeen.TryGetValue(curr, out var stored))
        {
            if (stored != pair)
            {
                junctions.Add(curr);
                // Drop the firstSeen entry: once a vertex is a junction, no future Observe call
                // looks at it. Keeps the dictionary from growing past O(non-junction vertices).
                firstSeen.Remove(curr);
            }
        }
        else
        {
            firstSeen[curr] = pair;
        }
    }

    // Lexicographic ordering on X, Y, Z, M — enough to give every distinct Position a stable
    // ordering for pair canonicalisation. Missing Z/M sort as -Inf so a 2D position never ties
    // with the same X/Y carrying a real Z/M.
    static bool Less(Position a, Position b)
    {
        if (a.X != b.X)
        {
            return a.X < b.X;
        }

        if (a.Y != b.Y)
        {
            return a.Y < b.Y;
        }

        var az = a.Z ?? double.NegativeInfinity;
        var bz = b.Z ?? double.NegativeInfinity;
        if (az != bz)
        {
            return az < bz;
        }

        var am = a.M ?? double.NegativeInfinity;
        var bm = b.M ?? double.NegativeInfinity;
        return am < bm;
    }

    static IReadOnlyList<Position> SimplifyRing(IReadOnlyList<Position> ring, HashSet<Position> junctions, double tolerance, SimplifyMethod method)
    {
        var n = RingCycleLength(ring);
        if (n < 3)
        {
            // Already too small to thin further — preserve the input shape (closure included).
            return ring;
        }

        // Indices of junction vertices within the cycle (the duplicated closure, if any, is past
        // the cycle and ignored). A ring with zero or one junction has no internal split point, so
        // the whole ring is one piece — simplify it directly as a closed ring (minPoints = 4 keeps
        // it valid). With ≥2 junctions, walk the cycle from the first junction round and back,
        // emitting one between-junctions arc per step.
        var junctionIndices = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (junctions.Contains(ring[i]))
            {
                junctionIndices.Add(i);
            }
        }

        if (junctionIndices.Count <= 1)
        {
            return LineSimplifier.Simplify(ring, tolerance, method, 4);
        }

        var result = new List<Position>();
        for (var j = 0; j < junctionIndices.Count; j++)
        {
            var start = junctionIndices[j];
            var end = junctionIndices[(j + 1) % junctionIndices.Count];
            var arc = ExtractArc(ring, start, end, n);
            var simplified = LineSimplifier.Simplify(arc, tolerance, method, 2);
            // First arc contributes its leading junction; subsequent arcs skip theirs (it's the
            // previous arc's trailing junction — appending it again would duplicate the seam).
            if (j == 0)
            {
                result.AddRange(simplified);
            }
            else
            {
                for (var k = 1; k < simplified.Count; k++)
                {
                    result.Add(simplified[k]);
                }
            }
        }

        // Each arc is thinned as an *open line*, so its floor is 2 points — its own two junctions.
        // Nothing in that per-arc floor knows it is assembling a ring, so a ring whose every arc
        // falls within the tolerance reassembles below a triangle. Two rings sharing one edge (each
        // splits into exactly two arcs between the same junction pair) collapse to (j0, j1, j0):
        // three positions, zero area, and an invalid linear ring — RFC 7946 §3.1.6 requires four.
        // The arc-wise answer is unusable at that point, so fall back to the closed-ring simplifier,
        // whose minPoints = 4 floors it at the minimal valid triangle exactly as plain Simplify does.
        if (IsDegenerateRing(result))
        {
            return LineSimplifier.Simplify(ring, tolerance, method, 4);
        }

        return result;
    }

    // A valid linear ring is four or more positions spanning at least three distinct vertices.
    // Reassembly always restores closure (the last arc ends on the first arc's leading junction),
    // so only the vertex count can fail. Both checks are needed: two arcs between one junction pair
    // give three positions, while a self-touching ring whose junction repeats can reach four
    // positions with only two distinct vertices.
    static bool IsDegenerateRing(List<Position> ring)
    {
        if (ring.Count < 4)
        {
            return true;
        }

        // Skip the closing vertex — it repeats the first by construction.
        var distinct = new HashSet<Position>();
        for (var i = 0; i < ring.Count - 1; i++)
        {
            distinct.Add(ring[i]);
        }

        return distinct.Count < 3;
    }

    static IReadOnlyList<Position> SimplifyLine(IReadOnlyList<Position> line, HashSet<Position> junctions, double tolerance, SimplifyMethod method)
    {
        if (line.Count < 3)
        {
            return line;
        }

        // Internal junctions (the endpoints are pinned as junctions by Classify but already
        // bookend the chain). Splitting at each lets a polyline that shares a stretch with
        // another polyline simplify that shared stretch identically on both.
        var junctionIndices = new List<int>
        {
            0
        };
        for (var i = 1; i < line.Count - 1; i++)
        {
            if (junctions.Contains(line[i]))
            {
                junctionIndices.Add(i);
            }
        }

        junctionIndices.Add(line.Count - 1);

        if (junctionIndices.Count == 2)
        {
            // No internal junctions — fall straight through to the open-line simplifier.
            return LineSimplifier.Simplify(line, tolerance, method, 2);
        }

        var result = new List<Position>();
        for (var j = 0; j + 1 < junctionIndices.Count; j++)
        {
            var start = junctionIndices[j];
            var end = junctionIndices[j + 1];
            var arc = new Position[end - start + 1];
            for (var k = 0; k <= end - start; k++)
            {
                arc[k] = line[start + k];
            }

            var simplified = LineSimplifier.Simplify(arc, tolerance, method, 2);
            if (j == 0)
            {
                result.AddRange(simplified);
            }
            else
            {
                for (var k = 1; k < simplified.Count; k++)
                {
                    result.Add(simplified[k]);
                }
            }
        }

        return result;
    }

    // Walks the ring's cycle from index start to index end (with wrap), returning the inclusive
    // sub-sequence of positions. end == start would be ambiguous on a cycle (no progress vs.
    // full lap); the caller never invokes this case because that's the "one junction" path
    // handled above as a whole-ring simplification.
    static Position[] ExtractArc(IReadOnlyList<Position> ring, int start, int end, int n)
    {
        var length = (end - start + n) % n;
        var arc = new Position[length + 1];
        for (var k = 0; k <= length; k++)
        {
            arc[k] = ring[(start + k) % n];
        }

        return arc;
    }
}
