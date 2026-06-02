/// <summary>
/// Maps longitude/latitude into pixel space: first through the chosen <see cref="MapProjection"/>
/// (planar coords), then a uniform scale that fits the projected extent into the canvas, centered,
/// with the Y axis flipped.
/// </summary>
sealed class Projection
{
    MapProjection kind;
    Envelope inputBounds;
    Envelope projectedBounds;
    double scale;
    double offsetX;
    double offsetY;
    LambertParameters? lambert;

    public Projection(Envelope bounds, RenderOptions options)
    {
        inputBounds = bounds;
        kind = Resolve(options.Projection, bounds);
        // Lambert's per-bounds parameters (standard parallels, reference origin) are derived once
        // from the input envelope; every ProjectPoint call reuses them. If the bounds degenerate to
        // a cone-flattening case (equator-symmetric or zero-height latitude span), the projection
        // silently falls back to PlateCarree so the renderer still produces a sensible image.
        if (kind == MapProjection.Lambert)
        {
            lambert = LambertParameters.TryFrom(bounds);
            if (lambert == null)
            {
                kind = MapProjection.PlateCarree;
            }
        }

        projectedBounds = ProjectEnvelope(bounds);
        var boundsWidth = projectedBounds.Width > 0 ? projectedBounds.Width : 1;
        var boundsHeight = projectedBounds.Height > 0 ? projectedBounds.Height : 1;

        if (options.MaxDimension > 0)
        {
            // Fit-to-box: the longer projected axis lands on MaxDimension, the shorter is derived
            // from the aspect ratio. Width/Height are ignored in this mode.
            if (boundsWidth >= boundsHeight)
            {
                Width = options.MaxDimension;
                Height = Math.Max(1, (int)Math.Round(options.MaxDimension * boundsHeight / boundsWidth));
            }
            else
            {
                Height = options.MaxDimension;
                Width = Math.Max(1, (int)Math.Round(options.MaxDimension * boundsWidth / boundsHeight));
            }
        }
        else
        {
            Width = options.Width;
            if (options.Height > 0)
            {
                Height = options.Height;
            }
            else
            {
                Height = Math.Max(1, (int) Math.Round(options.Width * boundsHeight / boundsWidth));
            }
        }

        var drawWidth = Math.Max(1, Width - 2 * options.Padding);
        var drawHeight = Math.Max(1, Height - 2 * options.Padding);
        scale = Math.Min(drawWidth / boundsWidth, drawHeight / boundsHeight);
        offsetX = (Width - boundsWidth * scale) / 2;
        offsetY = (Height - boundsHeight * scale) / 2;
    }

    public int Width { get; }

    public int Height { get; }

    public (double X, double Y) ToPixel(Position position)
    {
        var (projectedX, projectedY) = ProjectPoint(position.X, position.Y);
        return ToPixelFromProjected(projectedX, projectedY);
    }

    (double X, double Y)[] ToPixels(IReadOnlyList<Position> positions)
    {
        var result = new (double X, double Y)[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            result[i] = ToPixel(positions[i]);
        }

        return result;
    }

    /// <summary>
    /// One batch per output piece. For non-interrupted projections that's a single batch
    /// holding every input ring projected as-is; for <see cref="MapProjection.Goode"/> each
    /// input ring is clipped to each lobe's lon/lat bounds and the non-empty results are
    /// projected through that lobe's central meridian — one batch per lobe with content.
    /// <para>
    /// Each batch separates <see cref="PolygonBatch.Fill"/> (closed rings, for the rasterizer's
    /// even-odd fill) from <see cref="PolygonBatch.Strokes"/> (open polylines, with clip-edge
    /// segments removed for Goode so the stroke doesn't paint a visible "slice" along the lobe
    /// boundary where a continent was cut).
    /// </para>
    /// </summary>
    public IEnumerable<PolygonBatch> PreparePolygon(IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        if (kind != MapProjection.Goode)
        {
            var pixels = rings.Select(ToPixels).ToArray();
            // For non-interrupted projections fill and stroke trace the same rings — no
            // clipping happens, so every edge is "real".
            yield return new(pixels, pixels);
            yield break;
        }

        foreach (var lobe in GoodeLobes.AllLobes)
        {
            var fills = new List<(double X, double Y)[]>(rings.Count);
            var strokes = new List<(double X, double Y)[]>();
            foreach (var ring in rings)
            {
                // Clip the ring against *each* sub-rectangle of the lobe and project every
                // non-empty piece. Multi-rect lobes (the Greenland cut-out shape) emit
                // multiple pieces that share a central meridian, so the pieces meet
                // seamlessly in projected space. Internal seams between pieces are not
                // stroked because both endpoints share a boundary tag.
                foreach (var rect in lobe.Rects)
                {
                    var (vertices, tags) = GoodeLobes.ClipRingWithTags(ring, rect);
                    if (vertices.Count < 3)
                    {
                        // S-H can leave a sub-ring with <3 vertices if the polygon just
                        // grazes the rect; skip those — FillPolygon would draw a degenerate
                        // sliver and the stroke chains would emit zero-length edges.
                        continue;
                    }

                    var pixelRing = ToPixelsInLobe(vertices, lobe);
                    fills.Add(pixelRing);
                    foreach (var chain in GoodeLobes.BuildStrokeChains(pixelRing, tags))
                    {
                        strokes.Add(chain);
                    }
                }
            }

            if (fills.Count > 0)
            {
                yield return new(fills.ToArray(), strokes.ToArray());
            }
        }
    }

    /// <summary>
    /// One pixel subpath per lobe the input line crosses. For non-interrupted projections the
    /// line is yielded as a single projected subpath; for Goode, the line is split at every
    /// hemisphere and lon-lobe boundary it crosses so each emitted subpath stays inside one
    /// lobe and the stroke doesn't jump across an interrupt.
    /// </summary>
    public IEnumerable<(double X, double Y)[]> PrepareLine(IReadOnlyList<Position> positions)
    {
        if (kind != MapProjection.Goode)
        {
            yield return ToPixels(positions);
            yield break;
        }

        foreach (var subpath in GoodeLobes.SubdividePath(positions))
        {
            yield return ToPixelsInLobe(subpath.Positions, subpath.Lobe);
        }
    }

    /// <summary>
    /// The projection's world envelope as one or more closed rings in pixel space — what
    /// <see cref="RenderOptions.Ocean"/> paints under the features. For
    /// <see cref="MapProjection.Goode"/> that's six lobes; for the rectangular projections
    /// it's the input bounds (which for a non-linear projection like Lambert still curves on
    /// the canvas). Each ring is densely sampled along the input perimeter so non-linear
    /// projections capture the curvature instead of cutting corners.
    /// </summary>
    public IEnumerable<(double X, double Y)[]> GetWorldEnvelopeRings()
    {
        if (kind == MapProjection.Goode)
        {
            foreach (var lobe in GoodeLobes.AllLobes)
            {
                // Skip lobes the caller's bounds doesn't reach — a north-only render shouldn't
                // paint the southern lobes' envelopes.
                if (GoodeLobes.IntersectsBounds(lobe, inputBounds))
                {
                    yield return SampleLobePerimeter(lobe, samplesPerEdge: 32, openChain: false);
                }
            }

            yield break;
        }

        // Non-Goode: the input bounds *is* the world envelope. Sampling matters for projections
        // whose perimeter curves on the canvas (Lambert, WebMercator); for PlateCarree the four
        // corners would suffice, but sampling at 16 costs nothing and keeps the code uniform.
        yield return SampleEnvelopePerimeter(inputBounds, 16, ProjectPoint);
    }

    /// <summary>
    /// The projection's world envelope as open polylines suitable for stroking the outer
    /// border. For Goode each lobe's <c>lat=0</c> edge is omitted, so the equator doesn't
    /// render as a thick horizontal line bisecting the map — north and south lobes' top/bottom
    /// edges sit on the same projected y at the equator, and stroking both would double up.
    /// For other projections the closed ring is wrapped back to its first vertex so the
    /// stroke loops.
    /// </summary>
    public IEnumerable<(double X, double Y)[]> GetWorldEnvelopeStrokes()
    {
        if (kind == MapProjection.Goode)
        {
            foreach (var lobe in GoodeLobes.AllLobes)
            {
                if (GoodeLobes.IntersectsBounds(lobe, inputBounds))
                {
                    yield return SampleLobePerimeter(lobe, samplesPerEdge: 32, openChain: true);
                }
            }

            yield break;
        }

        // Non-Goode: the closed envelope, wrapped back to the first vertex so the stroke loops.
        foreach (var ring in GetWorldEnvelopeRings())
        {
            var closed = new (double X, double Y)[ring.Length + 1];
            Array.Copy(ring, closed, ring.Length);
            closed[^1] = ring[0];
            yield return closed;
        }
    }

    /// <summary>Walks the lobe's hand-coded perimeter clockwise in lon/lat, densely sampling
    /// each edge and projecting through the lobe's central meridian. For an open chain (the
    /// border stroke), the lat=0 equator edge is skipped — north and south lobes share that
    /// edge, so stroking it would double the equator line.</summary>
    (double X, double Y)[] SampleLobePerimeter(GoodeLobes.Lobe lobe, int samplesPerEdge, bool openChain)
    {
        var perimeter = lobe.Perimeter;
        var n = perimeter.Count;

        // For an open stroke chain, find the equator edge and start walking AFTER it. This
        // gives one contiguous chain of (n-1) edges in our lobe layouts (each lobe has
        // exactly one edge at lat=0). LINQ .First throws if no equator edge is present, which
        // would mean a malformed perimeter — preferable to silently rendering wrong output.
        var startEdge = 0;
        var edgeCount = n;
        if (openChain)
        {
            var equatorEdge = Enumerable.Range(0, n)
                .First(i => perimeter[i].Y == 0 && perimeter[(i + 1) % n].Y == 0);
            startEdge = (equatorEdge + 1) % n;
            edgeCount = n - 1;
        }

        var points = new List<(double X, double Y)>(edgeCount * samplesPerEdge + 1);
        for (var k = 0; k < edgeCount; k++)
        {
            var edgeIdx = (startEdge + k) % n;
            var from = perimeter[edgeIdx];
            var to = perimeter[(edgeIdx + 1) % n];
            // Emit samplesPerEdge points per edge (t = 0/N, 1/N, ..., (N-1)/N) — the next
            // edge's first sample picks up the endpoint. For the final edge of an open chain
            // we also emit the endpoint so the polyline reaches the lobe's final corner.
            var lastEdgeOfOpenChain = openChain && k == edgeCount - 1;
            var max = lastEdgeOfOpenChain ? samplesPerEdge : samplesPerEdge - 1;
            for (var j = 0; j <= max; j++)
            {
                var t = (double)j / samplesPerEdge;
                var lon = from.X + t * (to.X - from.X);
                var lat = from.Y + t * (to.Y - from.Y);
                var (px, py) = ProjectGoodeInLobe(lon, lat, lobe);
                points.Add(ToPixelFromProjected(px, py));
            }
        }

        return points.ToArray();
    }

    (double X, double Y)[] SampleEnvelopePerimeter(Envelope region, int samplesPerEdge, Func<double, double, (double X, double Y)> project) =>
        SampleEnvelopePerimeter((region.MinX, region.MaxX, region.MinY, region.MaxY), samplesPerEdge, project);

    (double X, double Y)[] SampleEnvelopePerimeter(
        (double LonMin, double LonMax, double LatMin, double LatMax) region,
        int samplesPerEdge,
        Func<double, double, (double X, double Y)> project)
    {
        // Walk the lon/lat rectangle clockwise, sampling each of the four edges. Each edge
        // omits its endpoint vertex (the next edge picks it up) so corners aren't duplicated.
        // The returned ring is in pixel space and ready for FillPolygon.
        var ring = new (double X, double Y)[samplesPerEdge * 4];
        var write = 0;
        for (var i = 0; i < samplesPerEdge; i++)
        {
            var t = (double)i / samplesPerEdge;
            ring[write++] = SampleEdge(region.LonMin, region.LatMin, region.LonMax, region.LatMin, t, project);
        }

        for (var i = 0; i < samplesPerEdge; i++)
        {
            var t = (double)i / samplesPerEdge;
            ring[write++] = SampleEdge(region.LonMax, region.LatMin, region.LonMax, region.LatMax, t, project);
        }

        for (var i = 0; i < samplesPerEdge; i++)
        {
            var t = (double)i / samplesPerEdge;
            ring[write++] = SampleEdge(region.LonMax, region.LatMax, region.LonMin, region.LatMax, t, project);
        }

        for (var i = 0; i < samplesPerEdge; i++)
        {
            var t = (double)i / samplesPerEdge;
            ring[write++] = SampleEdge(region.LonMin, region.LatMax, region.LonMin, region.LatMin, t, project);
        }

        return ring;
    }

    (double X, double Y) SampleEdge(double lonStart, double latStart, double lonEnd, double latEnd, double t, Func<double, double, (double X, double Y)> project)
    {
        var lon = lonStart + t * (lonEnd - lonStart);
        var lat = latStart + t * (latEnd - latStart);
        var (px, py) = project(lon, lat);
        return ToPixelFromProjected(px, py);
    }

    (double X, double Y)[] ToPixelsInLobe(IReadOnlyList<Position> positions, GoodeLobes.Lobe lobe)
    {
        // Project each lat/lon through the *specific* lobe (not FindLobe), then reuse the same
        // scale-and-centre transform as the regular pipeline. Going via the lobe directly is
        // essential at the clipped boundary, where the vertex sits exactly on the shared
        // meridian and FindLobe would deterministically pick one neighbour — projecting
        // through the wrong central meridian would put the boundary edge at the wrong x and
        // close the lobe in the wrong place.
        var result = new (double X, double Y)[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var (px, py) = ProjectGoodeInLobe(positions[i].X, positions[i].Y, lobe);
            result[i] = ToPixelFromProjected(px, py);
        }

        return result;
    }

    (double X, double Y) ToPixelFromProjected(double projectedX, double projectedY)
    {
        var x = offsetX + (projectedX - projectedBounds.MinX) * scale;
        var y = Height - offsetY - (projectedY - projectedBounds.MinY) * scale;
        return (x, y);
    }

    Envelope ProjectEnvelope(Envelope bounds)
    {
        switch (kind)
        {
            case MapProjection.WebMercator:
                // X is linear, Y is monotonic in latitude, so projecting the corners still suffices.
                return new(
                    bounds.MinX,
                    ProjectWebMercatorY(bounds.MinY),
                    bounds.MaxX,
                    ProjectWebMercatorY(bounds.MaxY));
            case MapProjection.Lambert:
            case MapProjection.Goode:
                // Lambert's parallels curve and meridians fan out; Goode's meridians taper toward
                // the poles inside the Mollweide caps. Either way the corners alone undershoot the
                // AABB, so sample the perimeter — 16 samples per edge captures the curvature without
                // visibly affecting fit (both projections are smooth).
                return SampleEnvelope(bounds, 16);
            default:
                // PlateCarree: X and Y are both linear in lon/lat, so the corners are the extreme.
                return bounds;
        }
    }

    Envelope SampleEnvelope(Envelope bounds, int samples)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        for (var i = 0; i <= samples; i++)
        {
            var t = (double)i / samples;
            var lon = bounds.MinX + t * (bounds.MaxX - bounds.MinX);
            var lat = bounds.MinY + t * (bounds.MaxY - bounds.MinY);
            Visit(lon, bounds.MinY);
            Visit(lon, bounds.MaxY);
            Visit(bounds.MinX, lat);
            Visit(bounds.MaxX, lat);
        }

        return new(minX, minY, maxX, maxY);

        void Visit(double lon, double lat)
        {
            var (px, py) = ProjectPoint(lon, lat);
            if (px < minX)
            {
                minX = px;
            }

            if (px > maxX)
            {
                maxX = px;
            }

            if (py < minY)
            {
                minY = py;
            }

            if (py > maxY)
            {
                maxY = py;
            }
        }
    }

    (double X, double Y) ProjectPoint(double longitude, double latitude)
    {
        switch (kind)
        {
            case MapProjection.WebMercator:
                return (longitude, ProjectWebMercatorY(latitude));
            case MapProjection.Lambert:
                return lambert!.Project(longitude, latitude);
            case MapProjection.Goode:
                return ProjectGoode(longitude, latitude);
            default:
                return (longitude, latitude);
        }
    }

    static double ProjectWebMercatorY(double latitude)
    {
        var clamped = Math.Clamp(latitude, -MapRenderer.WebMercatorMaxLatitude, MapRenderer.WebMercatorMaxLatitude);
        var radians = clamped * Math.PI / 180;
        // Scale back to degree-equivalent units so the projected envelope reads in the same unit as
        // longitude — the downstream pixel math is scale-invariant either way, but this keeps the
        // aspect ratio of a degree-square patch at the equator equal to 1 in both projections.
        return Math.Log(Math.Tan(Math.PI / 4 + radians / 2)) * 180 / Math.PI;
    }

    // Goode's Homolosine interrupted into 2 northern and 4 southern lobes (the conventional
    // land-favouring split — meridians of interrupt run through ocean basins so continents fall
    // inside lobes rather than spanning them). Within each lobe the projection is the classic
    // Homolosine: sinusoidal between ±transition latitude (40°44'11.8") and Mollweide outside
    // that band, joined with a small vertical offset to make y continuous at the seam. The
    // conventional transition latitude makes the x scale continuous too, so the seam reads as
    // smooth inside each lobe.
    const double goodeTransitionLatitude = 40.7368 * Math.PI / 180;
    static readonly double goodeTransitionTheta = SolveMollweideTheta(goodeTransitionLatitude);

    // The Mollweide y at the transition latitude minus the sinusoidal y at the same latitude —
    // subtract this from northern-hemisphere Mollweide y (add for southern) so the seam reads
    // smooth instead of jumping.
    static readonly double goodeYShift = Math.Sqrt(2) * Math.Sin(goodeTransitionTheta) - goodeTransitionLatitude;

    // Mollweide x = (2√2/π) · (λ − λ₀) · cos(θ). The constant is what makes the projection
    // equal-area on the unit sphere.
    static readonly double mollweideXFactor = 2 * Math.Sqrt(2) / Math.PI;

    static (double X, double Y) ProjectGoode(double longitude, double latitude) =>
        ProjectGoodeInLobe(longitude, latitude, GoodeLobes.FindLobe(longitude, latitude));

    static (double X, double Y) ProjectGoodeInLobe(double longitude, double latitude, GoodeLobes.Lobe lobe)
    {
        // The lobe's central meridian is the reference longitude for the projection within that
        // lobe; offset the input lon by it, project through the uninterrupted Homolosine, then
        // translate the result back so the lobe sits at its true geographic x at the equator
        // (where x_local = lon - centralMeridian, x_world = lon).
        var (xLocal, y) = ProjectGoodeUninterrupted(longitude - lobe.CentralMeridian, latitude);
        return (xLocal + lobe.CentralMeridian, y);
    }

    static (double X, double Y) ProjectGoodeUninterrupted(double longitude, double latitude)
    {
        // Clamp off the pole. Mollweide's auxiliary angle θ converges to ±π/2 at the pole, where
        // f'(θ) = 4cos²(θ) vanishes and Newton blows up; the clamp keeps the solver in its
        // well-conditioned interior. The 0.001° shaved off the pole is invisible at any sensible
        // image size.
        var phi = Math.Clamp(latitude, -89.999, 89.999) * Math.PI / 180;
        var lambda = longitude * Math.PI / 180;

        double xRad;
        double yRad;
        if (Math.Abs(phi) <= goodeTransitionLatitude)
        {
            // Sinusoidal — equal-area on the band around the equator. y is just latitude; x is
            // longitude scaled by cos(φ), so parallels stay straight and meridians curve in.
            xRad = lambda * Math.Cos(phi);
            yRad = phi;
        }
        else
        {
            // Mollweide caps — equal-area at higher latitudes. The y offset (sign-flipped per
            // hemisphere) glues the cap onto the sinusoidal band without a vertical jump.
            var theta = SolveMollweideTheta(phi);
            xRad = mollweideXFactor * lambda * Math.Cos(theta);
            var mollweideY = Math.Sqrt(2) * Math.Sin(theta);
            yRad = phi >= 0 ? mollweideY - goodeYShift : mollweideY + goodeYShift;
        }

        // Convert back to degree-equivalent units so the projected envelope reads in the same
        // scale as longitude — matches WebMercator's and Lambert's output units.
        return (xRad * 180 / Math.PI, yRad * 180 / Math.PI);
    }

    static double SolveMollweideTheta(double phi)
    {
        // Mollweide's auxiliary angle θ from 2θ + sin(2θ) = π sin(φ). Snyder's recommended
        // initial guess asin(2φ/π) is already inside the basin of attraction, so 8 Newton
        // iterations reach full double precision well off the pole — the upstream lat clamp
        // keeps φ off ±π/2 where f'(θ) = 4cos²(θ) collapses. A fixed loop avoids a convergence
        // branch the coverage gate would otherwise need a dedicated test for.
        var target = Math.PI * Math.Sin(phi);
        var theta = Math.Asin(2 * phi / Math.PI);
        for (var i = 0; i < 8; i++)
        {
            var f = 2 * theta + Math.Sin(2 * theta) - target;
            var derivative = 4 * Math.Cos(theta) * Math.Cos(theta);
            theta -= f / derivative;
        }

        return theta;
    }

    // Thresholds for Auto. Above the world cutoffs the bounds approach full-globe coverage and
    // Goode's equal-area Homolosine is the honest world projection; between the world and
    // regional cutoffs the data is continental and the LCC cone unfolds too far (parallels grow
    // visibly curved), so PlateCarree is the conventional fallback; under the regional cutoffs
    // Lambert is right. The cutoffs are deliberately conservative — Africa (latSpan ≈ 73°) routes
    // to PlateCarree, Asia (lonSpan ≈ 165°) stays PlateCarree, while a true world view (lonSpan
    // 360°) picks Goode.
    const double autoLatitudeSpanLimit = 60;
    const double autoLongitudeSpanLimit = 90;
    const double autoWorldLatitudeSpan = 90;
    const double autoWorldLongitudeSpan = 180;

    static MapProjection Resolve(MapProjection requested, Envelope bounds)
    {
        if (requested != MapProjection.Auto)
        {
            return requested;
        }

        if (bounds.Width >= autoWorldLongitudeSpan || bounds.Height >= autoWorldLatitudeSpan)
        {
            return MapProjection.Goode;
        }

        if (bounds.Width >= autoLongitudeSpanLimit || bounds.Height >= autoLatitudeSpanLimit)
        {
            return MapProjection.PlateCarree;
        }

        // Lambert handles its own degenerate cases (equator-symmetric or zero-span bounds) by
        // returning null from TryFrom, which the renderer then falls back to PlateCarree — so we
        // can pick Lambert unconditionally here and let that path handle the edge.
        return MapProjection.Lambert;
    }
}
