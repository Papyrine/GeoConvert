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
    /// and a Douglas–Peucker pass that would collapse below it falls back to the original.
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

        // Visvalingam stops at minPoints by construction; Douglas–Peucker can collapse a tiny shape to
        // its two (coincident, for a ring) endpoints under a large tolerance, so guard the floor here.
        return simplified.Count < minPoints ? points : simplified;
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
        var queue = new PriorityQueue<int, double>();
        for (var i = 1; i < count - 1; i++)
        {
            var area = TriangleArea(points[previous[i]], points[i], points[next[i]]);
            currentArea[i] = area;
            queue.Enqueue(i, area);
        }

        var alive = count;
        while (alive > minPoints && queue.TryDequeue(out var index, out var area))
        {
            if (removed[index] || area != currentArea[index])
            {
                // Stale entry: the vertex was already removed, or re-queued under a newer area after a
                // neighbour was dropped. The live entry (if any) is still in the heap behind this one.
                continue;
            }

            if (area >= minArea)
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

    static void Rearea(IReadOnlyList<Position> points, int[] previous, int[] next, double[] currentArea, PriorityQueue<int, double> queue, int index)
    {
        if (index <= 0 || index >= points.Count - 1)
        {
            // Endpoints are pinned and never carry an effective area, so there's nothing to re-queue.
            return;
        }

        var area = TriangleArea(points[previous[index]], points[index], points[next[index]]);
        currentArea[index] = area;
        queue.Enqueue(index, area);
    }

    static double TriangleArea(Position a, Position b, Position c) =>
        Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2;
}
