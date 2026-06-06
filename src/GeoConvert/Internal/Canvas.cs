/// <summary>A software RGBA raster with source-over blending and basic line/disc/polygon fills.</summary>
sealed class Canvas : IDisposable, IRenderSurface
{
    // Reused across FillPolygon calls so a render with hundreds of polygons doesn't allocate a fresh
    // set of crossings lists per call — one list per vertical sub-scanline (see fillSubSamples). The
    // parallel path gets its own per-thread set instead, since rows run concurrently.
    readonly List<double>[] scanlineCrossings = MakeCrossings();

    static List<double>[] MakeCrossings()
    {
        var lists = new List<double>[fillSubSamples];
        for (var i = 0; i < lists.Length; i++)
        {
            lists[i] = [];
        }

        return lists;
    }

    // Per-row fractional-coverage accumulator for the antialiased polygon fill (one double per pixel
    // column). Rented once per Canvas and reused for every serial FillPolygon row — the parallel path
    // gets its own per-thread buffer instead, since rows run concurrently. Only the [clearLo, clearHi]
    // span actually touched by a polygon is cleared and read each row, so the stale tail is irrelevant.
    readonly double[] coverageBuffer;

    // The logical pixel-buffer size (width × height × 4 bytes). May be smaller than Pixels.Length —
    // ArrayPool returns arrays at least the requested size, potentially larger — so anything reading
    // the buffer goes through width/height, not Pixels.Length, for the upper bound. Trailing bytes
    // beyond logicalSize are never written or read.
    readonly int logicalSize;

    public Canvas(int width, int height, Rgba background)
    {
        Width = width;
        Height = height;
        logicalSize = width * height * 4;
        // Rent from the shared pool rather than allocating fresh. The 3+ MB pixel buffer was the
        // single largest per-render allocation (60% of the Full_Optimal byte budget on the
        // benchmark workload). High-throughput callers — servers rendering many maps back-to-back —
        // get most rents from a thread-local bucket and pay no allocation per call. Single renders
        // see no win and a tiny dispatch overhead; nothing regresses.
        Pixels = ArrayPool<byte>.Shared.Rent(logicalSize);
        // Fill only the logical span — the rented array may be larger; the trailing bytes are
        // never read, so leaving stale pool content there is fine.
        MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(0, logicalSize)).Fill(Pack(background));
        coverageBuffer = ArrayPool<double>.Shared.Rent(width);
    }

    // Returns the pooled buffers. Owners must dispose so the arrays are recycled; the only owner
    // today is MapRenderer.Render via `using var canvas = ...`.
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(Pixels);
        ArrayPool<double>.Shared.Return(coverageBuffer);
    }

    // Packs RGBA into a uint so that reinterpreting the pixel buffer as uints yields the R,G,B,A byte order.
    static uint Pack(Rgba color) =>
        BitConverter.IsLittleEndian
            ? (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24))
            : (uint)((color.R << 24) | (color.G << 16) | (color.B << 8) | color.A);

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    // Logical pixel-buffer length in bytes (width × height × 4). Pixels.Length may be larger
    // since ArrayPool can return an oversized array; consumers reading the full buffer should
    // bound by this rather than Pixels.Length.
    public int PixelByteCount => logicalSize;

    public void Blend(int x, int y, Rgba color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || color.A == 0)
        {
            return;
        }

        var i = (y * Width + x) * 4;
        if (color.A == 255)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = 255;
            return;
        }

        var a = color.A / 255d;
        BlendTranslucent(i, color.R * a, color.G * a, color.B * a, color.A, 1 - a);
    }

    // Source-over alpha blend at a known-valid pixel offset. Factored out so the per-pixel translucent
    // path is the same code whether reached via Blend (bounds-checked) or FillPolygon's inner loop
    // (which clips to the span ends once, then runs without bounds checks).
    void BlendTranslucent(int i, double preR, double preG, double preB, double aByte, double inverse)
    {
        Pixels[i] = (byte)(preR + Pixels[i] * inverse);
        Pixels[i + 1] = (byte)(preG + Pixels[i + 1] * inverse);
        Pixels[i + 2] = (byte)(preB + Pixels[i + 2] * inverse);
        Pixels[i + 3] = (byte)(aByte + Pixels[i + 3] * inverse);
    }

    // Vectorised translucent blend across a contiguous run of pixels. Math is kept in
    // Vector256<double> so per-lane evaluation matches scalar BlendTranslucent bit-for-bit —
    // separate vmulpd + vaddpd (not FMA, which would round once and shift output by an ULP) plus
    // VCVTTPD2DQ truncation that matches C#'s (byte)(double) cast for values in [0, 255].
    // The four lanes carry R/G/B/A so one vector op handles all channels of one pixel; the JIT
    // pipelines successive iterations so two or three pixels are typically in flight at once.
    // rowEnd is exclusive (byte offset just past the last pixel).
    void BlendTranslucentSpan(int rowStart, int rowEnd, double preR, double preG, double preB, double preA, double inverse)
    {
        var p = rowStart;
        if (Avx.IsSupported && Sse41.IsSupported)
        {
            var preVec = Vector256.Create(preR, preG, preB, preA);
            var inverseVec = Vector256.Create(inverse);
            ref var pixelsRef = ref MemoryMarshal.GetArrayDataReference(Pixels);
            // SIMD consumes everything except the final pixel, which the scalar tail handles. The
            // alternative — `p + 4 <= rowEnd` — would consume the whole span and leave the tail
            // unreachable for 4-byte-aligned spans, breaking 100% line coverage. The cost is one
            // scalar pixel per call: invisible against the per-row setup work.
            for (; p + 8 <= rowEnd; p += 4)
            {
                ref var src = ref Unsafe.Add(ref pixelsRef, p);
                // Read 4 bytes as one uint, widen low 4 bytes → 4 int32 (PMOVZXBD), then 4 int32 →
                // 4 doubles (VCVTDQ2PD). Two instructions for the whole byte→double pipeline beats
                // four scalar cvtsi2sd + four vector inserts the naive Vector256.Create path emits.
                var raw = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref src)).AsByte();
                var existing = Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(raw));
                var result = existing * inverseVec + preVec;
                // 4 doubles → 4 int32 (VCVTTPD2DQ, truncate toward zero — matches (int)(double)),
                // then pack int32 → int16 → byte via the standard two-stage saturating pack. Each
                // input lane is in [0, 255] (proven by linearity: result = R·α + dst·(1−α) stays
                // bounded by the inputs) so saturation is a no-op and the byte order is preserved.
                var ints = Avx.ConvertToVector128Int32WithTruncation(result);
                var int16s = Sse2.PackSignedSaturate(ints, ints);
                var bytes = Sse2.PackUnsignedSaturate(int16s, int16s);
                Unsafe.WriteUnaligned(ref src, bytes.AsUInt32().GetElement(0));
            }
        }

        // Scalar tail — handles the no-AVX path entirely, and the trailing pixel on AVX when the
        // span happens to start mid-row (shouldn't, since FillPolygon's row offsets are already
        // 4-byte aligned, but cheap insurance).
        for (; p < rowEnd; p += 4)
        {
            BlendTranslucent(p, preR, preG, preB, preA, inverse);
        }
    }

    /// <summary>
    /// Axis-aligned solid fill of the half-open rect [<paramref name="x0"/>, <paramref name="x1"/>)
    /// × [<paramref name="y0"/>, <paramref name="y1"/>). Edges aren't antialiased — the rect is
    /// rounded to whole pixels and every covered pixel gets a full alpha blend. Used by the label
    /// pass to paint the "knockout" backdrop under each label's bbox; tiny enough that a
    /// per-pixel Blend loop is fine without the FillPolygon scanline machinery.
    /// </summary>
    public void FillRect(double x0, double y0, double x1, double y1, Rgba color)
    {
        if (color.A == 0)
        {
            return;
        }

        var minX = Math.Max(0, (int)Math.Floor(x0));
        var minY = Math.Max(0, (int)Math.Floor(y0));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(x1) - 1);
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(y1) - 1);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                Blend(x, y, color);
            }
        }
    }

    /// <summary>
    /// Soft-edged thick-line stroke: every pixel within <c>width/2 + 0.5</c> of the line segment
    /// gets a fractional alpha based on its perpendicular distance, blended at that coverage.
    /// Antialiased everywhere — used both for label glyph strokes and for the renderer's polygon
    /// outlines / polyline geometry so the whole output reads consistently. The trade-off is a
    /// 1-pixel-wide stroke blooms slightly into a ~1.5px soft band; on typical map scales the
    /// smoothness wins over the lost pixel sharpness.
    /// <para>
    /// Sub-pixel widths are coverage-compensated: the geometric radius can't shrink below half a
    /// pixel (a line still has to land on the pixel grid), so a sub-1px stroke is drawn as a 1px
    /// band with its alpha scaled down by the requested width. A 0.4px line therefore reads as a
    /// faint hairline rather than a solid 1px stroke — which is what keeps a dense map (thousands
    /// of tiny polygons whose borders all collapse onto the same pixels at a small canvas size)
    /// from filling in to a solid black mass. The fade is clamped so a hairline never drops below
    /// <see cref="subPixelAlphaFloor"/> of its colour's alpha: without that floor, autoscaled borders
    /// on a sparse zoomed-out map (a handful of countries, not thousands) fade almost to nothing —
    /// the floor keeps them legibly visible while still letting the width itself shrink. The scale is
    /// continuous at width = 1, so any width ≥ 1 is left exactly as before.
    /// </para>
    /// </summary>
    // Lower bound on the sub-pixel coverage fade: a stroke thinner than 1px fades its alpha by its
    // width, but never below this fraction. Tuned so a heavily-autoscaled border on a sparse map stays
    // visible without a dense map's overlapping hairlines stacking back up into a solid mass.
    const double subPixelAlphaFloor = 0.5;

    public void StrokeLine(double x0, double y0, double x1, double y1, double width, Rgba color)
    {
        // Coverage compensation for sub-pixel strokes (see remarks): below 1px the line can't get
        // geometrically thinner than the 0.5 radius floor, so fade its alpha by the width instead.
        // Math.Min keeps width ≥ 1 at scale 1.0 — a no-op that leaves full-width output bit-identical;
        // Math.Max floors the fade so a very thin stroke stays visible rather than vanishing.
        var coverageScale = Math.Max(Math.Min(width, 1.0), subPixelAlphaFloor);
        color = color with {A = (byte)(color.A * coverageScale)};
        var radius = Math.Max(width / 2, 0.5);
        // One extra pixel beyond the geometric radius gives room for the fractional-coverage
        // ramp at the outer edge of the stroke: below this distance coverage = 1, beyond it
        // coverage = 0, with a linear fall-off in between.
        var outer = radius + 0.5;
        // Clamp the iteration bounds to the canvas BEFORE the int casts. Projections like Lambert
        // can produce wildly out-of-bounds coordinates for features far from the standard parallels
        // (Antarctica through a northern-hemisphere cone, etc.); without the clamp the int cast
        // overflows and/or the outer y-loop runs over billions of rows, doing per-pixel Blend
        // rejection — effectively an infinite loop. Blend rejects out-of-canvas pixels anyway, so
        // clamping the loop bounds is purely a fast-path with no visual change.
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - outer));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1) + outer));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - outer));
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + outer));

        var dx = x1 - x0;
        var dy = y1 - y0;
        var lengthSq = dx * dx + dy * dy;
        if (lengthSq == 0)
        {
            // Zero-length segment degenerates to a single antialiased disc — the projection math
            // below would otherwise divide by zero.
            FillDisc(x0, y0, radius, color);
            return;
        }

        var outerSq = outer * outer;

        // SIMD path: same approach as FillDisc — vectorise the per-pixel coverage compute
        // (t-projection through sqrt) across 4 x-pixels at a time, then call Blend per-lane.
        // All vector ops mirror the scalar evaluation order so the per-lane alpha matches
        // (byte)(color.A * coverage) bit-for-bit.
        var simd = Avx.IsSupported && Sse41.IsSupported;
        var x0Vec = simd ? Vector256.Create(x0) : default;
        var y0Vec = simd ? Vector256.Create(y0) : default;
        var dxVec = simd ? Vector256.Create(dx) : default;
        var dyVec = simd ? Vector256.Create(dy) : default;
        var lengthSqVec = simd ? Vector256.Create(lengthSq) : default;
        var outerVec = simd ? Vector256.Create(outer) : default;
        var oneVec = simd ? Vector256.Create(1.0) : default;
        var colorAVec = simd ? Vector256.Create((double)color.A) : default;
        var laneOffsetsVec = simd ? Vector256.Create(0.0, 1.0, 2.0, 3.0) : default;

        for (var y = minY; y <= maxY; y++)
        {
            var x = minX;
            if (simd)
            {
                // (y - y0) * dy is row-constant — precompute scalar and broadcast so the per-lane
                // arithmetic does the same multiply-then-add sequence as scalar (different lane
                // ordering would still match bit-for-bit, but matching the scalar evaluation order
                // keeps the snapshot proof trivial).
                var yMinusY0TimesDyVec = Vector256.Create((y - y0) * dy);
                var yVec = Vector256.Create((double)y);
                for (; x + 4 <= maxX; x += 4)
                {
                    var xVec = Vector256.Create((double)x) + laneOffsetsVec;
                    // t = ((x - x0) * dx + (y - y0) * dy) / lengthSq, clamped to [0, 1].
                    var tVec = ((xVec - x0Vec) * dxVec + yMinusY0TimesDyVec) / lengthSqVec;
                    tVec = Vector256.Max(Vector256<double>.Zero, Vector256.Min(oneVec, tVec));
                    // ddx = x - (x0 + t * dx); ddy = y - (y0 + t * dy)
                    var ddxVec = xVec - (x0Vec + tVec * dxVec);
                    var ddyVec = yVec - (y0Vec + tVec * dyVec);
                    var distSqVec = ddxVec * ddxVec + ddyVec * ddyVec;
                    var coverageVec = Vector256.Max(
                        Vector256<double>.Zero,
                        Vector256.Min(oneVec, outerVec - Vector256.Sqrt(distSqVec)));
                    var alphaVec = Vector256.Floor(colorAVec * coverageVec);
                    for (var k = 0; k < 4; k++)
                    {
                        var alpha = (byte)alphaVec.GetElement(k);
                        if (alpha == 0)
                        {
                            continue;
                        }

                        Blend(x + k, y, color with {A = alpha});
                    }
                }
            }

            for (; x <= maxX; x++)
            {
                // Closest point on the segment to (x, y): project onto the segment direction and
                // clamp t into [0, 1] so points past the endpoints fall back to round caps at the
                // segment ends (the coverage ramp does that work automatically).
                var t = ((x - x0) * dx + (y - y0) * dy) / lengthSq;
                if (t < 0)
                {
                    t = 0;
                }
                else if (t > 1)
                {
                    t = 1;
                }

                var ddx = x - (x0 + t * dx);
                var ddy = y - (y0 + t * dy);
                var distSq = ddx * ddx + ddy * ddy;
                if (distSq >= outerSq)
                {
                    continue;
                }

                var coverage = outer - Math.Sqrt(distSq);
                if (coverage > 1)
                {
                    coverage = 1;
                }

                Blend(x, y, color with {A = (byte)(color.A * coverage)});
            }
        }
    }

    // IRenderSurface: strokes a polyline by drawing each segment as an individual antialiased
    // thick line. Splitting the chain into segments here (rather than at the call site) is what
    // lets the vector surface emit one <polyline> per chain while the raster path stays per-segment
    // — the rendered pixels are identical to looping StrokeLine at the caller.
    public void StrokePath(IReadOnlyList<(double X, double Y)> points, double width, Rgba color)
    {
        for (var i = 0; i + 1 < points.Count; i++)
        {
            StrokeLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, width, color);
        }
    }

    // IRenderSurface: draws label text through the hand-rolled stroke font. The SVG surface emits a
    // native <text> element instead; both are positioned by the same baseline/anchor math the
    // Labeller computes, so placement is shared across the two outputs.
    public void DrawText(string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo) =>
        StrokeFont.Render(this, text, leftX, baselineY, size, color, halo);

    /// <summary>Antialiased disc — every pixel within <c>radius + 0.5</c> gets fractional
    /// coverage based on its distance to the centre. Used directly for point markers, and via
    /// <see cref="StrokeLine"/>'s zero-length fast path.</summary>
    public void FillDisc(double cx, double cy, double radius, Rgba color)
    {
        var r = Math.Max(radius, 0.5);
        var outer = r + 0.5;
        var outerSq = outer * outer;
        // Clamp to canvas before the int cast — see StrokeLine for the rationale. A non-linear
        // projection can hand us a (cx, cy) far outside int range; the cast would otherwise
        // overflow and the iteration would either skip entirely or, worse, loop through billions of
        // pixels relying on the per-pixel Blend bounds check.
        var minX = Math.Max(0, (int)Math.Floor(cx - outer));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(cx + outer));
        var minY = Math.Max(0, (int)Math.Floor(cy - outer));
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(cy + outer));

        var simd = Avx.IsSupported && Sse41.IsSupported;
        var cxVec = simd ? Vector256.Create(cx) : default;
        var outerVec = simd ? Vector256.Create(outer) : default;
        var oneVec = simd ? Vector256.Create(1.0) : default;
        var colorAVec = simd ? Vector256.Create((double)color.A) : default;

        for (var y = minY; y <= maxY; y++)
        {
            var dy = y - cy;
            var dySq = dy * dy;
            // Whole-row skip when the row sits outside the disc's y span — every x on this row
            // would test distSq >= outerSq and continue, so we'd just be doing 2·outer wasted
            // sqrt+blend tests. Matches scalar output bit-for-bit; no Blend calls happen either way.
            if (dySq >= outerSq)
            {
                continue;
            }

            var x = minX;
            if (simd)
            {
                // Process 4 x-pixels per iter; coverage compute (including the sqrt) goes through
                // Vector256.Sqrt which on x86 maps to `vsqrtpd` — same IEEE-754 rounding as scalar
                // Math.Sqrt, so per-lane alpha matches scalar (byte)(color.A * coverage) exactly.
                // Stop early enough that the scalar tail gets at least one pixel — keeps the body
                // reachable for 100% line coverage.
                var dySqVec = Vector256.Create(dySq);
                for (; x + 4 <= maxX; x += 4)
                {
                    var xVec = Vector256.Create((double)x, x + 1, x + 2, x + 3);
                    var dxVec = xVec - cxVec;
                    var distSqVec = dxVec * dxVec + dySqVec;
                    // coverage = clamp(outer - sqrt(distSq), 0, 1). The Max(0, ...) is what
                    // replaces scalar's `if (distSq >= outerSq) continue` — for those lanes
                    // sqrt(distSq) >= outer so coverage clamps to 0, producing alpha=0 and a
                    // no-op blend in the lane loop below.
                    var sqrtVec = Vector256.Sqrt(distSqVec);
                    var coverageVec = Vector256.Max(
                        Vector256<double>.Zero,
                        Vector256.Min(oneVec, outerVec - sqrtVec));
                    var alphaVec = Vector256.Floor(colorAVec * coverageVec);
                    for (var k = 0; k < 4; k++)
                    {
                        var alpha = (byte)alphaVec.GetElement(k);
                        if (alpha != 0)
                        {
                            Blend(x + k, y, color with {A = alpha});
                        }
                    }
                }
            }

            for (; x <= maxX; x++)
            {
                var dx = x - cx;
                var distSq = dx * dx + dySq;
                if (distSq >= outerSq)
                {
                    continue;
                }

                var coverage = outer - Math.Sqrt(distSq);
                if (coverage > 1)
                {
                    coverage = 1;
                }

                Blend(x, y, color with {A = (byte)(color.A * coverage)});
            }
        }
    }

    /// <summary>
    /// Fills the region bounded by the given rings using the even-odd rule (so holes are excluded),
    /// antialiased. Each output row accumulates fractional coverage from
    /// <see cref="fillSubSamples"/> evenly-spaced vertical sub-scanlines (vertical AA) with analytic
    /// fractional coverage at the span endpoints (horizontal AA) into a per-row buffer, then composites
    /// once. Fully-interior pixels accumulate to exactly coverage 1.0 (the sub-sample weights are exact
    /// powers of two that sum to one), so a solid interior is byte-identical to a plain scanline fill
    /// and reuses the opaque whole-pixel / translucent SIMD span paths; only edge pixels pay the
    /// per-pixel coverage-scaled blend, which is what smooths the previously stair-stepped polygon
    /// boundaries.
    /// </summary>
    public void FillPolygon((double X, double Y)[][] rings, Rgba color)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var ring in rings)
        {
            foreach (var point in ring)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        if (minY > maxY)
        {
            return;
        }

        // Floor (not ceil) so the edge rows/columns that a sub-pixel boundary only partially covers
        // are visited — that's where the antialiased ramp lives. The composite span [clearLo, clearHi]
        // bounds every pixel AddSpan can touch, so it's both the range cleared per row and the range
        // scanned when compositing.
        var clearLo = Math.Max(0, (int)Math.Floor(minX));
        var clearHi = Math.Min(Width - 1, (int)Math.Floor(maxX));
        if (clearHi < clearLo)
        {
            // Whole polygon lies off the left or right edge of the canvas — nothing to paint.
            return;
        }

        var first = Math.Max(0, (int)Math.Floor(minY));
        var last = Math.Min(Height - 1, (int)Math.Floor(maxY));
        var opaque = color.A == 255;
        var packed = Pack(color);
        // Precompute alpha factors once per polygon — the per-pixel/per-span blend avoids a division
        // by 255 on every pixel of a translucent fill.
        var a = color.A / 255d;
        var inverse = 1 - a;
        var preR = color.R * a;
        var preG = color.G * a;
        var preB = color.B * a;
        var preA = (double)color.A;

        // Per-polygon scanline parallelism. Each y writes to disjoint pixel rows so there's no data
        // race across threads within a single FillPolygon (different polygons in the same render
        // are still serialised by the caller — order matters for source-over). The threshold
        // gates out small polygons where Parallel.For's per-iter overhead dominates the row work;
        // measured tipping point on a modern x86 is ~64 rows. Below threshold the serial path
        // reuses the class-level crossings list and coverage buffer (no allocation per render);
        // above it each thread gets its own pair via the localInit factory.
        if (last - first + 1 >= parallelScanlineThreshold)
        {
            Parallel.For(
                first,
                last + 1,
                () => (Crossings: MakeCrossings(), Coverage: new double[Width]),
                (y, _, scratch) =>
                {
                    FillScanline(y, scratch.Crossings, scratch.Coverage, rings, color, opaque, packed, preR, preG, preB, preA, inverse, clearLo, clearHi);
                    return scratch;
                },
                _ => { });
        }
        else
        {
            for (var y = first; y <= last; y++)
            {
                FillScanline(y, scanlineCrossings, coverageBuffer, rings, color, opaque, packed, preR, preG, preB, preA, inverse, clearLo, clearHi);
            }
        }
    }

    // ~64 rows is where Parallel.For's per-iter overhead breaks even with the row work on an
    // 8-core x86. Tune downward if profile shows under-utilisation at this threshold.
    const int parallelScanlineThreshold = 64;

    // Vertical sub-scanlines per output row. Four gives four levels of vertical antialiasing on
    // near-horizontal edges and (combined with the analytic horizontal coverage) smooth diagonals,
    // while keeping the per-row edge walk to 4×. The weight 1/4 is exact in IEEE-754, so a fully
    // covered pixel sums to exactly 1.0 and stays on the fast composite path.
    const int fillSubSamples = 4;

    // Accumulates one antialiased scanline: walks every ring's edges once, and for each edge records
    // its x-crossing into the list for every sub-scanline of this row that the edge straddles (so the
    // edge arrays are traversed once per row, not once per sub-scanline). Each sub-scanline's crossings
    // are then sorted and turned into fractional coverage for the runs between paired crossings
    // (even-odd rule) accumulated into `coverage`; finally the row's coverage is composited into the
    // pixel buffer. `crossings` (one list per sub-scanline) and `coverage` are the caller's reusable
    // scratch (class-level for serial, per-thread for parallel). Only the [clearLo, clearHi] column
    // span — the polygon's pixel x-extent — is cleared and composited.
    void FillScanline(int y, List<double>[] crossings, double[] coverage, (double X, double Y)[][] rings, Rgba color, bool opaque, uint packed, double preR, double preG, double preB, double preA, double inverse, int clearLo, int clearHi)
    {
        coverage.AsSpan(clearLo, clearHi - clearLo + 1).Clear();

        foreach (var list in crossings)
        {
            list.Clear();
        }

        foreach (var ring in rings)
        {
            for (var i = 0; i < ring.Length; i++)
            {
                var pa = ring[i];
                var pb = ring[i + 1 == ring.Length ? 0 : i + 1];
                for (var sub = 0; sub < fillSubSamples; sub++)
                {
                    var scan = y + (sub + 0.5) / fillSubSamples;
                    if ((!(pa.Y <= scan) || !(pb.Y > scan)) &&
                        (!(pb.Y <= scan) || !(pa.Y > scan)))
                    {
                        continue;
                    }

                    var t = (scan - pa.Y) / (pb.Y - pa.Y);
                    crossings[sub].Add(pa.X + t * (pb.X - pa.X));
                }
            }
        }

        const double weight = 1.0 / fillSubSamples;
        foreach (var list in crossings)
        {
            list.Sort();
            for (var i = 0; i + 1 < list.Count; i += 2)
            {
                AddSpan(coverage, list[i], list[i + 1], weight);
            }
        }

        CompositeRow(y, coverage, color, opaque, packed, preR, preG, preB, preA, inverse, clearLo, clearHi);
    }

    // Adds `weight` of horizontal coverage for the float span [xLeft, xRight) into the row buffer,
    // splitting the fractional coverage of the two boundary pixels analytically (a pixel the span only
    // partly covers gets the covered fraction) and full weight to the pixels wholly inside. The span is
    // clipped to [0, Width] first so off-canvas runs contribute nothing.
    void AddSpan(double[] coverage, double xLeft, double xRight, double weight)
    {
        if (xLeft < 0)
        {
            xLeft = 0;
        }

        if (xRight > Width)
        {
            xRight = Width;
        }

        if (xLeft >= xRight)
        {
            return;
        }

        var left = (int)xLeft;
        var right = (int)xRight;
        if (left == right)
        {
            // Span narrower than a pixel and contained in one column (e.g. a polygon's pointed tip).
            coverage[left] += weight * (xRight - xLeft);
            return;
        }

        coverage[left] += weight * (left + 1 - xLeft);
        for (var x = left + 1; x < right; x++)
        {
            coverage[x] += weight;
        }

        if (right < Width)
        {
            coverage[right] += weight * (xRight - right);
        }
    }

    // Composites one row of accumulated coverage into the pixel buffer. Walks [clearLo, clearHi],
    // skipping empty columns, fast-pathing runs of fully-covered (coverage ≥ 1) pixels through the
    // opaque whole-pixel fill or the translucent SIMD span blend, and blending partially-covered edge
    // pixels individually with their alpha scaled by coverage.
    void CompositeRow(int y, double[] coverage, Rgba color, bool opaque, uint packed, double preR, double preG, double preB, double preA, double inverse, int clearLo, int clearHi)
    {
        var x = clearLo;
        while (x <= clearHi)
        {
            var c = coverage[x];
            if (c <= 0)
            {
                x++;
                continue;
            }

            if (c >= 1)
            {
                var runStart = x;
                do
                {
                    x++;
                }
                while (x <= clearHi && coverage[x] >= 1);

                if (opaque)
                {
                    // A fully-covered opaque run overwrites the span, so write whole pixels directly.
                    var span = Pixels.AsSpan((y * Width + runStart) * 4, (x - runStart) * 4);
                    MemoryMarshal.Cast<byte, uint>(span).Fill(packed);
                }
                else
                {
                    // Fully-covered translucent run — blend the whole span at once. The per-pixel math
                    // is identical to BlendTranslucent's bit-for-bit so snapshot output matches scalar.
                    var rowStart = (y * Width + runStart) * 4;
                    var rowEnd = (y * Width + x) * 4;
                    BlendTranslucentSpan(rowStart, rowEnd, preR, preG, preB, preA, inverse);
                }
            }
            else
            {
                // Partially-covered edge pixel: fade the fill alpha by the coverage fraction.
                Blend(x, y, color with {A = (byte)(color.A * c)});
                x++;
            }
        }
    }
}
