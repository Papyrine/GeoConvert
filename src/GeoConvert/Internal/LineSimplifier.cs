/// <summary>
/// Vertex-reduction for ordered position sequences (line and ring coordinate lists) — the lossy
/// "compression" pass behind <see cref="Simplifier"/>. Both algorithms always keep the first and last
/// vertex, so an open line never drops below 2 points and a closed ring keeps its shared start/end
/// vertex (closure survives). The two endpoints of a ring coincide, which makes the initial
/// Douglas–Peucker chord degenerate; <see cref="PerpendicularDistanceSquared"/> handles that by
/// falling back to point distance, so the first split still anchors on the farthest vertex.
/// </summary>
static class LineSimplifier
{
    /// <summary>
    /// Simplifies <paramref name="points"/> with the chosen <paramref name="method"/>, never returning
    /// fewer than <paramref name="minPoints"/> vertices (2 for an open line, 4 for a closed ring — a
    /// triangle plus its closing vertex). Inputs already at or below that floor are returned unchanged,
    /// and a Douglas–Peucker pass that would collapse a ring below it is reduced to a minimal valid
    /// triangle (see <see cref="MinimalRing"/>) rather than restored to full detail.
    /// </summary>
    public static IReadOnlyList<Position> Simplify(IReadOnlyList<Position> points, double tolerance, SimplifyMethod method, int minPoints)
    {
        if (points.Count <= minPoints)
        {
            return points;
        }

        var simplified = method switch
        {
            SimplifyMethod.DouglasPeucker => DouglasPeucker(points, tolerance),
            SimplifyMethod.Visvalingam => Visvalingam(points, tolerance, minPoints),
            _ => throw new GeoConvertException($"Unknown simplify method '{method}'."),
        };

        if (simplified.Count >= minPoints)
        {
            return simplified;
        }

        // The pass collapsed the shape below the minimum valid vertex count. Open lines (minPoints 2)
        // always keep their two endpoints, so this only happens for a closed ring (minPoints 4) whose
        // whole extent fell within the tolerance. Returning the original would *restore full detail* —
        // the opposite of simplifying — and for data full of sub-tolerance rings (island chains) that
        // inverts the contract: a larger tolerance would yield a *larger* output. Instead reduce the
        // ring to its minimal valid form: the triangle spanned by its three most extreme vertices.
        return MinimalRing(points) ?? points;
    }

    /// <summary>
    /// Builds the smallest valid closed ring that still spans <paramref name="points"/>'s extent — the
    /// triangle through its first vertex, the vertex farthest from it, and the vertex farthest from that
    /// chord — emitted in original traversal order so the ring's winding is preserved. Returns null when
    /// no non-degenerate triangle exists (every vertex coincident or collinear), leaving the caller to
    /// keep the already-degenerate original rather than fabricate a zero-area ring.
    /// </summary>
    static List<Position>? MinimalRing(IReadOnlyList<Position> points)
    {
        var anchor = points[0];

        var farthest = 0;
        var maxFromAnchor = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - anchor.X;
            var dy = points[i].Y - anchor.Y;
            var distance = dx * dx + dy * dy;
            if (distance > maxFromAnchor)
            {
                maxFromAnchor = distance;
                farthest = i;
            }
        }

        if (farthest == 0)
        {
            // Every vertex coincides with the first — there is no extent to span.
            return null;
        }

        var apex = 0;
        var maxFromChord = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            var distance = PerpendicularDistanceSquared(points[i], anchor, points[farthest]);
            if (distance > maxFromChord)
            {
                maxFromChord = distance;
                apex = i;
            }
        }

        if (apex == 0)
        {
            // Collinear: no third vertex sits off the chord, so no triangle has area.
            return null;
        }

        // Order the two chosen vertices by their original index so the reduced ring traverses the same
        // way as the source (the anchor is index 0, ahead of both); closure repeats the anchor.
        var (second, third) = farthest < apex ? (farthest, apex) : (apex, farthest);
        return [anchor, points[second], points[third], anchor];
    }

    static List<Position> DouglasPeucker(IReadOnlyList<Position> points, double tolerance)
    {
        var last = points.Count - 1;
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[last] = true;
        var toleranceSquared = tolerance * tolerance;

        // Explicit stack rather than recursion: a pathological near-collinear input recurses once per
        // vertex, which would overflow the call stack on a large polyline.
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, last));
        while (pending.Count > 0)
        {
            var (first, segmentEnd) = pending.Pop();
            var maxDistance = 0d;
            var farthest = -1;
            var start = points[first];
            var end = points[segmentEnd];
            for (var i = first + 1; i < segmentEnd; i++)
            {
                var distance = PerpendicularDistanceSquared(points[i], start, end);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    farthest = i;
                }
                else if (farthest != -1 &&
                         distance == maxDistance &&
                         ComparePositions(points[i], points[farthest]) < 0)
                {
                    // Exact perpendicular-distance tie: break it on the vertex's own coordinates, not
                    // its index. "Keep the earliest index" flips when the same arc is traversed the
                    // other way, so two rings sharing a border would keep mirror vertices and
                    // TopologySimplifier's bit-identical-shared-arc guarantee would crack a hairline
                    // gap. ComparePositions is reversal-invariant, so both directions keep the same one.
                    farthest = i;
                }
            }

            if (farthest != -1 && maxDistance > toleranceSquared)
            {
                keep[farthest] = true;
                pending.Push((first, farthest));
                pending.Push((farthest, segmentEnd));
            }
        }

        var result = new List<Position>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    static double PerpendicularDistanceSquared(Position point, Position lineStart, Position lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        if (dx == 0 && dy == 0)
        {
            // Degenerate chord — the endpoints coincide (a closed ring's shared first/last vertex).
            // Use the straight-line distance from the candidate to that shared vertex instead.
            dx = point.X - lineStart.X;
            dy = point.Y - lineStart.Y;
            return dx * dx + dy * dy;
        }

        var numerator = dx * (lineStart.Y - point.Y) - (lineStart.X - point.X) * dy;
        return numerator * numerator / (dx * dx + dy * dy);
    }

    static List<Position> Visvalingam(IReadOnlyList<Position> points, double minArea, int minPoints)
    {
        var count = points.Count;
        var previous = new int[count];
        var next = new int[count];
        for (var i = 0; i < count; i++)
        {
            previous[i] = i - 1;
            next[i] = i + 1;
        }

        // A min-heap keyed on effective area gives the cheapest vertex to drop in O(log n). The BCL
        // PriorityQueue has no decrease-key, so a recomputed vertex is re-enqueued and its prior entry
        // left to be skipped on pop (lazy deletion) — currentArea tracks each vertex's live area.
        var removed = new bool[count];
        var currentArea = new double[count];
        // Priority is (area, vertex): the vertex breaks exact area ties by coordinate so the removal
        // order — and therefore the surviving set — is reversal-invariant. The BCL PriorityQueue's own
        // tie order is unspecified and flips under reversal, which would break TopologySimplifier's
        // bit-identical-shared-arc guarantee just as an index tie-break would (see ComparePositions).
        var queue = new PriorityQueue<int, (double Area, Position Vertex)>(AreaThenPositionComparer.Instance);
        for (var i = 1; i < count - 1; i++)
        {
            var area = TriangleArea(points[previous[i]], points[i], points[next[i]]);
            currentArea[i] = area;
            queue.Enqueue(i, (area, points[i]));
        }

        var alive = count;
        while (alive > minPoints && queue.TryDequeue(out var index, out var priority))
        {
            if (removed[index] || priority.Area != currentArea[index])
            {
                // Stale entry: the vertex was already removed, or re-queued under a newer area after a
                // neighbour was dropped. The live entry (if any) is still in the heap behind this one.
                continue;
            }

            if (priority.Area >= minArea)
            {
                // The global minimum is at/above the threshold, so every surviving vertex is too.
                break;
            }

            removed[index] = true;
            alive--;
            var before = previous[index];
            var after = next[index];
            next[before] = after;
            previous[after] = before;
            Rearea(points, previous, next, currentArea, queue, before);
            Rearea(points, previous, next, currentArea, queue, after);
        }

        var result = new List<Position>(alive);
        for (var i = 0; i < count; i++)
        {
            if (!removed[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    static void Rearea(IReadOnlyList<Position> points, int[] previous, int[] next, double[] currentArea, PriorityQueue<int, (double Area, Position Vertex)> queue, int index)
    {
        if (index <= 0 || index >= points.Count - 1)
        {
            // Endpoints are pinned and never carry an effective area, so there's nothing to re-queue.
            return;
        }

        var area = TriangleArea(points[previous[index]], points[index], points[next[index]]);
        currentArea[index] = area;
        queue.Enqueue(index, (area, points[index]));
    }

    static double TriangleArea(Position a, Position b, Position c) =>
        Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2;

    // A total order on vertices that ignores which direction an arc is walked — the reversal-invariant
    // tie-breaker both simplifiers use so a border shared by two rings thins to the *same* vertices from
    // either side (TopologySimplifier's whole reason to exist). Comparing coordinates does that; comparing
    // indices does not, because the shared arc runs forward in one ring and backward in the other. Missing
    // Z/M sort as -Inf so a 2D vertex never ties with an otherwise-equal 3D one.
    static int ComparePositions(Position a, Position b)
    {
        var byX = a.X.CompareTo(b.X);
        if (byX != 0)
        {
            return byX;
        }

        var byY = a.Y.CompareTo(b.Y);
        if (byY != 0)
        {
            return byY;
        }

        var byZ = (a.Z ?? double.NegativeInfinity).CompareTo(b.Z ?? double.NegativeInfinity);
        if (byZ != 0)
        {
            return byZ;
        }

        return (a.M ?? double.NegativeInfinity).CompareTo(b.M ?? double.NegativeInfinity);
    }

    // Orders the Visvalingam heap by effective area, then by vertex to break exact ties deterministically
    // and reversal-invariantly. Two distinct vertices can share an area; a vertex re-queued under a new
    // area compares by that area against its live value, so lazy-deletion still works.
    sealed class AreaThenPositionComparer : IComparer<(double Area, Position Vertex)>
    {
        public static readonly AreaThenPositionComparer Instance = new();

        public int Compare((double Area, Position Vertex) x, (double Area, Position Vertex) y)
        {
            var byArea = x.Area.CompareTo(y.Area);
            return byArea != 0 ? byArea : ComparePositions(x.Vertex, y.Vertex);
        }
    }
}
