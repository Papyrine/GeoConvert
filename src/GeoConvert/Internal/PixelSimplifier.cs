/// <summary>
/// Douglas–Peucker vertex reduction in canvas pixel space — the size-shrinking pass behind
/// <see cref="RenderOptions.SvgSimplifyTolerance"/>. It operates on the renderer's projected
/// <c>(X, Y)</c> pixel tuples rather than lon/lat <see cref="Position"/>s (the geometry-model
/// <see cref="LineSimplifier"/> is the lon/lat counterpart), so the tolerance is measured in output
/// pixels and a sub-pixel value is visually lossless at the rendered size. The first and last vertex
/// are always kept, so a closed ring (whose shared first/last vertex makes the initial chord
/// degenerate) stays closed.
/// </summary>
static class PixelSimplifier
{
    public static (double X, double Y)[] Simplify(IReadOnlyList<(double X, double Y)> points, double tolerance)
    {
        var count = points.Count;
        if (count < 3)
        {
            // Nothing to drop between the two pinned endpoints (or fewer).
            return [.. points];
        }

        var last = count - 1;
        var keep = new bool[count];
        keep[0] = true;
        keep[last] = true;
        var toleranceSquared = tolerance * tolerance;

        // Explicit stack rather than recursion: a pathological near-collinear input recurses once per
        // vertex, which would overflow the call stack on a large polyline (mirrors LineSimplifier).
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

        var result = new List<(double X, double Y)>(count);
        for (var i = 0; i < count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return [.. result];
    }

    static double PerpendicularDistanceSquared((double X, double Y) point, (double X, double Y) lineStart, (double X, double Y) lineEnd)
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
}
