/// <summary>
/// Lobe geometry for Goode's *interrupted* Homolosine: the conventional 2-north, 4-south split
/// that runs the interrupt meridians through ocean basins so the major continents fall whole
/// inside one lobe. Holds the lobe table and the polygon/polyline clipping needed before
/// projection (so the rasterizer can fill and stroke each lobe's contribution independently
/// without painting across the inter-lobe gaps).
/// </summary>
static class GoodeLobes
{
    /// <summary>One axis-aligned lon/lat rectangle making up part of a lobe. A lobe with two
    /// rects represents a non-rectangular logical region (e.g. an L-shape with a tab or a
    /// notch); the two rects share an edge in lon/lat space, and the renderer projects both
    /// through the lobe's single central meridian so the pieces meet seamlessly on the
    /// canvas.</summary>
    public readonly record struct Rect(double LonMin, double LonMax, double LatMin, double LatMax);

    /// <summary>One logical lobe: a central meridian shared by every sub-rectangle and a
    /// hand-coded perimeter (clockwise corner list in lon/lat) for the outer envelope. The
    /// perimeter is the union outline of the rects — for a single-rect lobe it's the rect's
    /// four corners; for an L-shape it walks the L.</summary>
    public readonly record struct Lobe(
        double CentralMeridian,
        IReadOnlyList<Rect> Rects,
        IReadOnlyList<Position> Perimeter);

    // Goode's interrupted Homolosine with the conventional ocean-meridian cuts, plus a
    // *Greenland cut-out* on the north interruption: at lat ≥ 60° the cut steps from lon=-40°
    // east to lon=-10°, capturing Greenland (lon ≈ -73° to -12°) inside the Americas lobe so
    // it renders adjacent to Canada — Greenland is geographically Canada's neighbour, separated
    // from Europe by the Greenland Sea, so this anchoring reads more naturally than the
    // Eurasian-side variant. Iceland (lon ≈ -22°, lat ≈ 65°) goes with Greenland as a
    // consequence; the cut at -10° leaves continental Europe intact in the eastern lobe.
    public static readonly Lobe[] AllLobes =
    [
        // North west (Americas + Greenland + Iceland) — tab extends *east* above lat=60 to
        // pull Greenland into the Americas lobe.
        new(
            CentralMeridian: -100,
            Rects:
            [
                new(LonMin: -180, LonMax: -40, LatMin:  0, LatMax: 60),
                new(LonMin: -180, LonMax: -10, LatMin: 60, LatMax: 90),
            ],
            Perimeter:
            [
                new(-40, 0), new(-180, 0), new(-180, 90), new(-10, 90), new(-10, 60), new(-40, 60),
            ]),

        // North east (Eurasia + Africa-N) — main rect for lower lat, retracted rect for upper
        // lat (cut moved east from -40° to -10° to make room for Greenland in the west lobe).
        new(
            CentralMeridian: 30,
            Rects:
            [
                new(LonMin: -40, LonMax: 180, LatMin:  0, LatMax: 60),
                new(LonMin: -10, LonMax: 180, LatMin: 60, LatMax: 90),
            ],
            Perimeter:
            [
                new(180, 0), new(-40, 0), new(-40, 60), new(-10, 60), new(-10, 90), new(180, 90),
            ]),

        // South: four single-rectangle lobes covering S-America, S-Africa, Australia, and the
        // Pacific. Central meridians chosen at the centre of each landmass.
        new(
            CentralMeridian: -160,
            Rects: [new(LonMin: -180, LonMax: -100, LatMin: -90, LatMax: 0)],
            Perimeter: [new(-100, 0), new(-180, 0), new(-180, -90), new(-100, -90)]),
        new(
            CentralMeridian: -60,
            Rects: [new(LonMin: -100, LonMax: -20, LatMin: -90, LatMax: 0)],
            Perimeter: [new(-20, 0), new(-100, 0), new(-100, -90), new(-20, -90)]),
        new(
            CentralMeridian: 20,
            Rects: [new(LonMin: -20, LonMax: 80, LatMin: -90, LatMax: 0)],
            Perimeter: [new(80, 0), new(-20, 0), new(-20, -90), new(80, -90)]),
        new(
            CentralMeridian: 140,
            Rects: [new(LonMin: 80, LonMax: 180, LatMin: -90, LatMax: 0)],
            Perimeter: [new(180, 0), new(80, 0), new(80, -90), new(180, -90)]),
    ];

    /// <summary>The lobe a single point belongs to. Walks every sub-rect of every lobe — a
    /// point on a shared boundary (e.g. lon=-40° at lat=30°) lands in the first-matched lobe,
    /// which is fine because both lobes' projections agree along their shared edge.</summary>
    public static Lobe FindLobe(double longitude, double latitude)
    {
        foreach (var lobe in AllLobes)
        {
            foreach (var rect in lobe.Rects)
            {
                if (longitude >= rect.LonMin && longitude <= rect.LonMax &&
                    latitude >= rect.LatMin && latitude <= rect.LatMax)
                {
                    return lobe;
                }
            }
        }

        // Malformed input fell outside [-180, 180] × [-90, 90]; fall back to the lobe whose
        // central meridian is closest in lon so the projection still produces a finite point
        // instead of throwing. The renderer prefers a graceful degraded output over a crash.
        var best = AllLobes[0];
        var bestDistance = double.PositiveInfinity;
        foreach (var lobe in AllLobes)
        {
            // Each lobe is entirely in one hemisphere, so the first rect's lat sign identifies
            // it. Skip lobes on the wrong hemisphere so the fallback picks a sensible neighbour.
            if (latitude >= 0 != lobe.Rects[0].LatMin >= 0)
            {
                continue;
            }

            var distance = Math.Abs(longitude - lobe.CentralMeridian);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = lobe;
            }
        }

        return best;
    }

    /// <summary>True if any of the lobe's sub-rectangles intersects the input bounds. Used to
    /// skip lobes that aren't covered by the requested render extent.</summary>
    public static bool IntersectsBounds(Lobe lobe, Envelope bounds)
    {
        foreach (var rect in lobe.Rects)
        {
            if (rect.LonMax > bounds.MinX && rect.LonMin < bounds.MaxX &&
                rect.LatMax > bounds.MinY && rect.LatMin < bounds.MaxY)
            {
                return true;
            }
        }

        return false;
    }

    // Reusable scratch buffers for the clip. Clipping a single ring allocates nothing: the four
    // half-plane passes ping-pong between buffer A and buffer B, and the densify step writes into
    // a third pair. Marked [ThreadStatic] so concurrent RenderPng calls on different threads each
    // get their own set — within one render DrawLayer is single-threaded, so reuse across rings is
    // safe. The returned lists ARE the densify scratch pair, so the contract is: the result of one
    // ClipRingWithTags call is valid only until the next call on the same thread. Every caller
    // (PreparePolygon) consumes both lists fully — copying vertices into a pixel array and reading
    // tags via BuildStrokeChains — before the next clip, so the reuse is invisible to them.
    [ThreadStatic] static List<Position>? scratchAVertices;
    [ThreadStatic] static List<int>? scratchATags;
    [ThreadStatic] static List<Position>? scratchBVertices;
    [ThreadStatic] static List<int>? scratchBTags;
    [ThreadStatic] static List<Position>? scratchOutVertices;
    [ThreadStatic] static List<int>? scratchOutTags;

    /// <summary>Sutherland-Hodgman clip of a polygon ring against the lobe's lon/lat AABB.
    /// Returns the clipped vertices along with a bitmask per vertex indicating which lobe
    /// boundary planes the vertex was introduced by (0 = original input vertex; non-zero =
    /// intersection point on one of the four lobe boundaries). The caller uses the tags to
    /// distinguish *real* polygon edges from clip-edge segments that close the clipped piece
    /// along the lobe boundary — real edges get stroked, clip edges don't, so a clipped
    /// continent reads as one shape rather than several with vertical slice marks inside.
    /// <para>The returned lists are reused per-thread scratch (see the buffer fields above) and
    /// are only valid until the next <see cref="ClipRingWithTags"/> call on the same thread.</para></summary>
    public static (List<Position> Vertices, List<int> BoundaryTags) ClipRingWithTags(IReadOnlyList<Position> ring, Rect rect)
    {
        var bufferAVertices = scratchAVertices ??= [];
        var bufferATags = scratchATags ??= [];
        var bufferBVertices = scratchBVertices ??= [];
        var bufferBTags = scratchBTags ??= [];

        // Seed buffer A with the input ring; every vertex starts tagged 0 (an original, un-clipped
        // vertex). Manual loop rather than AddRange to avoid an enumerator allocation for the
        // IReadOnlyList input.
        bufferAVertices.Clear();
        bufferATags.Clear();
        for (var i = 0; i < ring.Count; i++)
        {
            bufferAVertices.Add(ring[i]);
            bufferATags.Add(0);
        }

        // Each pass is one half-plane; intersection vertices it introduces are tagged with the bit
        // for that boundary. A vertex's tag accumulates across passes only if it gets re-intersected
        // on a subsequent plane, which for axis-aligned planes only happens at the lobe corners —
        // and corner-on-corner edges aren't a real concern. The passes ping-pong A→B→A→B→A so the
        // final result lands back in buffer A without any per-pass allocation.
        ClipHalfPlaneTagged(
            bufferAVertices, bufferATags, bufferBVertices, bufferBTags,
            Axis.Longitude, rect.LonMin, keepGreater: true, introducedTag: 1);
        ClipHalfPlaneTagged(
            bufferBVertices, bufferBTags, bufferAVertices, bufferATags,
            Axis.Longitude, rect.LonMax, keepGreater: false, introducedTag: 2);
        ClipHalfPlaneTagged(
            bufferAVertices, bufferATags, bufferBVertices, bufferBTags,
            Axis.Latitude, rect.LatMin, keepGreater: true, introducedTag: 4);
        ClipHalfPlaneTagged(
            bufferBVertices, bufferBTags, bufferAVertices, bufferATags,
            Axis.Latitude, rect.LatMax, keepGreater: false, introducedTag: 8);

        // Densify clip edges so the polygon's projected fill follows the lobe boundary's
        // Mollweide curve. The clip edge in lon/lat is a straight line (constant lon or lat)
        // with only two endpoints; projected through the lobe's central meridian, those two
        // points become two pixel-space points and the rasterizer draws a *straight* line
        // between them — but the lobe's actual border curves inward toward the pole. Adding
        // intermediate vertices along the straight lon/lat line lets each be projected
        // individually, so the resulting fill edge traces the lobe's curve and there's no
        // gap between a polygon like Antarctica and the lobe outline.
        return DensifyClipEdges(bufferAVertices, bufferATags);
    }

    // Bit mask of the two longitude-boundary tags (LonMin=1, LonMax=2). A clip edge on one of these
    // planes runs along a meridian (constant lon, varying lat) and curves when projected; an edge on
    // a latitude plane (LatMin=4, LatMax=8) runs along a parallel, which projects to a straight
    // horizontal line in the pseudocylindrical lobe and needs no densification.
    const int longitudeBoundaryMask = 1 | 2;

    // One densify sample per this many degrees of latitude span — matches the old flat 16-samples-
    // over-a-90°-meridian density (~5.6°/sample) without over-sampling short edges.
    const double densifyDegreesPerSample = 6;

    static (List<Position>, List<int>) DensifyClipEdges(List<Position> verts, List<int> tags)
    {
        // Output goes to the dedicated scratch pair (distinct from the A/B clip buffers this reads
        // from); returned to the caller, valid until the next ClipRingWithTags call.
        var output = scratchOutVertices ??= [];
        var outputTags = scratchOutTags ??= [];
        output.Clear();
        outputTags.Clear();
        var count = verts.Count;
        for (var i = 0; i < count; i++)
        {
            var current = verts[i];
            var nextIndex = (i + 1) % count;
            var next = verts[nextIndex];
            var currentTag = tags[i];
            var nextTag = tags[nextIndex];
            output.Add(current);
            outputTags.Add(currentTag);

            // Only edges where both endpoints share a boundary tag are clip-introduced edges
            // worth densifying — original polygon edges already trace the input geometry as
            // densely as the caller chose.
            var sharedTag = currentTag & nextTag;
            if (sharedTag == 0)
            {
                continue;
            }

            // Latitude-plane clip edges project to a straight horizontal line (parallels don't
            // curve), so two endpoints are exact — skip densifying them entirely. Only meridian
            // edges curve, and the number of samples scales with the latitude span they cover.
            if ((sharedTag & longitudeBoundaryMask) == 0)
            {
                continue;
            }

            var samples = Math.Max(1, (int)Math.Ceiling(Math.Abs(next.Y - current.Y) / densifyDegreesPerSample));
            for (var j = 1; j < samples; j++)
            {
                var t = (double)j / samples;
                output.Add(new(
                    current.X + t * (next.X - current.X),
                    current.Y + t * (next.Y - current.Y)));
                outputTags.Add(sharedTag);
            }
        }

        return (output, outputTags);
    }

    // Every distinct lon/lat plane that bounds a lobe sub-rect. A straight lon/lat segment can only
    // change lobe by crossing one of these, so together they are the complete candidate set for a
    // split. Derived from AllLobes so the two can't drift apart if the lobe table is retuned.
    static readonly double[] boundaryLongitudes = DistinctSorted(AllLobes.SelectMany(_ => _.Rects).SelectMany(_ => new[] { _.LonMin, _.LonMax }));
    static readonly double[] boundaryLatitudes = DistinctSorted(AllLobes.SelectMany(_ => _.Rects).SelectMany(_ => new[] { _.LatMin, _.LatMax }));

    static double[] DistinctSorted(IEnumerable<double> values) => [.. values.Distinct().Order()];

    // A run this short is treated as empty: two boundary planes were crossed at the same point (the
    // segment passed exactly through a lobe corner), so the run has no interior to sample a lobe
    // from — its midpoint would be the ambiguous corner itself.
    const double minimumRunLength = 1e-12;

    // One boundary plane crossing, in segment parameter space. Axis+Plane are carried alongside T so
    // the split vertex can be snapped exactly onto the plane rather than reconstructed from T (both
    // lobes project that vertex, and it has to land on their shared edge in each).
    readonly record struct Crossing(double T, Axis Axis, double Plane);

    /// <summary>Splits a polyline at every lobe boundary it crosses. Each emitted subpath is a
    /// contiguous run of vertices in one lobe (the boundary intersection is inserted at both
    /// ends of the split so the strokes reach all the way to the lobe edge before the gap).
    /// <para>A single segment can cross any number of boundaries, on either axis: the north
    /// lobes are divided by a meridian below lat 60° and a different one above it (the Greenland
    /// cut-out), so a due-north line changes lobe across a *parallel*; and a long segment can
    /// step over an entire lobe, or — because the north lobes are L-shaped — leave one lobe and
    /// re-enter it. So each segment is walked plane by plane instead of being judged by the
    /// lobes of its two endpoints.</para></summary>
    public static IEnumerable<(Lobe Lobe, List<Position> Positions)> SubdividePath(IReadOnlyList<Position> positions)
    {
        if (positions.Count < 2)
        {
            yield break;
        }

        // A straight segment crosses each plane at most once, so this is sized for the worst case
        // and reused across the path's segments. Allocated rather than stack-allocated because an
        // iterator body can't stackalloc, and reused per-path rather than kept in a [ThreadStatic]
        // because two SubdividePath enumerations can be alive on one thread.
        var crossings = new Crossing[boundaryLongitudes.Length + boundaryLatitudes.Length];
        var currentLobe = FindLobe(positions[0].X, positions[0].Y);
        var current = new List<Position> { positions[0] };
        for (var i = 1; i < positions.Count; i++)
        {
            var previous = positions[i - 1];
            var next = positions[i];
            var crossingCount = CollectCrossings(previous, next, crossings);

            // Walk the runs between consecutive crossings. A run's lobe is read from its midpoint,
            // because the crossing points themselves sit exactly on a boundary where FindLobe is
            // ambiguous. Most crossings don't actually change lobe — lon=-40° bounds the north
            // lobes but runs through the middle of the southern S-America lobe — so only a run
            // whose lobe differs from the one we're in produces a split.
            var runStart = 0d;
            for (var run = 0; run <= crossingCount; run++)
            {
                var runEnd = run == crossingCount ? 1 : crossings[run].T;
                if (runEnd - runStart < minimumRunLength)
                {
                    runStart = runEnd;
                    continue;
                }

                var midpoint = Lerp(previous, next, (runStart + runEnd) / 2);
                var lobe = FindLobe(midpoint.X, midpoint.Y);
                if (!lobe.Equals(currentLobe))
                {
                    if (runStart == 0)
                    {
                        // `previous` is itself the boundary point, and is already the last vertex
                        // of the current subpath. When it's also the *only* one — the path begins
                        // on a boundary, and FindLobe resolved the tie to the lobe it's heading
                        // out of — there's no stroke to emit, just a lobe to correct.
                        if (current.Count > 1)
                        {
                            yield return (currentLobe, current);
                            current = [previous];
                        }
                    }
                    else
                    {
                        var crossing = crossings[run - 1];
                        var split = Intersect(previous, next, crossing.Axis, crossing.Plane);
                        current.Add(split);
                        yield return (currentLobe, current);
                        current = [split];
                    }

                    currentLobe = lobe;
                }

                runStart = runEnd;
            }

            current.Add(next);
        }

        yield return (currentLobe, current);
    }

    // Fills <paramref name="crossings"/> with the parameters at which the segment strictly crosses a
    // lobe boundary plane, ascending, and returns how many there were.
    static int CollectCrossings(Position a, Position b, Crossing[] crossings)
    {
        var count = 0;
        foreach (var longitude in boundaryLongitudes)
        {
            if (TryCrossing(a.X, b.X, longitude, out var t))
            {
                crossings[count] = new(t, Axis.Longitude, longitude);
                count++;
            }
        }

        foreach (var latitude in boundaryLatitudes)
        {
            if (TryCrossing(a.Y, b.Y, latitude, out var t))
            {
                crossings[count] = new(t, Axis.Latitude, latitude);
                count++;
            }
        }

        // Insertion sort. The two groups arrive individually ordered only for an eastward/northward
        // segment, and there are at most a handful of crossings, so anything fancier is overhead.
        for (var i = 1; i < count; i++)
        {
            var value = crossings[i];
            var j = i - 1;
            while (j >= 0 && crossings[j].T > value.T)
            {
                crossings[j + 1] = crossings[j];
                j--;
            }

            crossings[j + 1] = value;
        }

        return count;
    }

    // The parameter at which the segment crosses <paramref name="plane"/>, or false when it stays on
    // one side. A segment that merely touches the plane with an endpoint doesn't cross it: the
    // parameter would be 0 or 1, which bounds a run rather than splitting one.
    static bool TryCrossing(double from, double to, double plane, out double t)
    {
        if (!(from < plane && to > plane) &&
            !(from > plane && to < plane))
        {
            t = 0;
            return false;
        }

        t = (plane - from) / (to - from);
        return true;
    }

    static Position Lerp(Position a, Position b, double t) =>
        new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));

    // Which coordinate a half-plane bounds. Each lobe AABB is clipped by two longitude planes and
    // two latitude planes, so the plane is fully described by an axis + threshold + direction —
    // no per-plane delegate needed.
    enum Axis
    {
        Longitude,
        Latitude,
    }

    // Clips one axis-aligned half-plane, reading the ring from the source buffers and writing the
    // kept/intersection vertices into the (cleared) destination buffers. No allocation and no
    // delegate indirection — the plane is described by <paramref name="axis"/> (which coordinate it
    // bounds), <paramref name="threshold"/> (the bound), and <paramref name="keepGreater"/> (keep
    // the side at/above the threshold, vs at/below). The caller supplies both buffer pairs and
    // ping-pongs them across the four passes.
    static void ClipHalfPlaneTagged(
        List<Position> sourceVertices,
        List<int> sourceTags,
        List<Position> destinationVertices,
        List<int> destinationTags,
        Axis axis,
        double threshold,
        bool keepGreater,
        int introducedTag)
    {
        destinationVertices.Clear();
        destinationTags.Clear();
        var count = sourceVertices.Count;
        if (count == 0)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var current = sourceVertices[i];
            var previous = sourceVertices[(i + count - 1) % count];
            var currentInside = Inside(current, axis, threshold, keepGreater);
            var previousInside = Inside(previous, axis, threshold, keepGreater);
            if (previousInside != currentInside)
            {
                destinationVertices.Add(Intersect(previous, current, axis, threshold));
                destinationTags.Add(introducedTag);
            }

            if (currentInside)
            {
                destinationVertices.Add(current);
                destinationTags.Add(sourceTags[i]);
            }
        }
    }

    static bool Inside(Position position, Axis axis, double threshold, bool keepGreater)
    {
        var value = axis == Axis.Longitude ? position.X : position.Y;
        return keepGreater ? value >= threshold : value <= threshold;
    }

    static Position Intersect(Position a, Position b, Axis axis, double threshold)
    {
        if (axis == Axis.Longitude)
        {
            return InterpolateToX(a, b, threshold);
        }

        return InterpolateToY(a, b, threshold);
    }

    /// <summary>Walks a clipped ring and yields the maximal runs of consecutive non-clip edges
    /// as open polylines. An edge from vertex i to vertex (i+1)%n counts as a clip edge when
    /// both endpoints carry an overlapping boundary tag — i.e. both sit on the same lobe-AABB
    /// plane, the hallmark of an edge that S-H added to close the clipped piece along the
    /// lobe boundary rather than something tracing the original polygon.</summary>
    public static IEnumerable<(double X, double Y)[]> BuildStrokeChains((double X, double Y)[] ring, IReadOnlyList<int> tags)
    {
        // Callers (PreparePolygon) already filter rings with < 3 vertices, so the loop body
        // always sees a usable ring.
        var chain = new List<(double X, double Y)>();
        for (var i = 0; i < ring.Length; i++)
        {
            var next = (i + 1) % ring.Length;
            var isClipEdge = (tags[i] & tags[next]) != 0;
            if (chain.Count == 0)
            {
                chain.Add(ring[i]);
            }

            if (isClipEdge)
            {
                if (chain.Count >= 2)
                {
                    yield return chain.ToArray();
                }

                chain = new();
            }
            else
            {
                chain.Add(ring[next]);
            }
        }

        if (chain.Count >= 2)
        {
            yield return chain.ToArray();
        }
    }

    static Position InterpolateToX(Position a, Position b, double x)
    {
        // Linear lon/lat interpolation along the segment. The segment can be vertical (a.X ==
        // b.X), but in that case it doesn't cross a vertical boundary, so this overload isn't
        // called with that input — guarded by the half-plane sign-change check.
        var t = (x - a.X) / (b.X - a.X);
        return new(x, a.Y + t * (b.Y - a.Y));
    }

    static Position InterpolateToY(Position a, Position b, double y)
    {
        var t = (y - a.Y) / (b.Y - a.Y);
        return new(a.X + t * (b.X - a.X), y);
    }
}
