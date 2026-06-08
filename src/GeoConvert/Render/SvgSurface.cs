/// <summary>
/// Vector <see cref="IRenderSurface"/>: accumulates the renderer's geometry, ocean and label passes
/// as SVG markup rather than rasterising them. Each primitive maps to one SVG element — polygons to
/// a <c>&lt;path&gt;</c> with <c>fill-rule="evenodd"</c> (so holes punch through), strokes to a
/// <c>&lt;polyline&gt;</c>, point markers to a <c>&lt;circle&gt;</c>, and labels to a native
/// <c>&lt;text&gt;</c>. The result shares the projection, styling, stroke auto-scaling and label
/// placement of the PNG path; only the output encoding differs.
/// <para>
/// Coordinates are emitted in canvas pixel space, matching the <c>viewBox</c>, rounded to two
/// decimals to keep the document compact and deterministic (so snapshots are stable). Labels use
/// the viewer's <c>sans-serif</c> font sized to the cap height, positioned by the same baseline and
/// anchor the <see cref="Labeller"/> computes — so glyph shapes (and therefore exact extents) depend
/// on the rendering client, but placement and collision are identical to the raster renderer, which
/// reserves its boxes from the hand-rolled stroke font's metrics.
/// </para>
/// </summary>
sealed class SvgSurface(int width, int height, Rgba background, double simplifyTolerance) :
    IRenderSurface
{
    StringBuilder body = new();

    public int Width { get; } = width;

    public int Height { get; } = height;

    public void FillPolygon((double X, double Y)[][] rings, Rgba color)
    {
        // A fully transparent fill paints nothing — skip it rather than emit an invisible element.
        if (color.A == 0)
        {
            return;
        }

        body.Append("<path d=\"");
        foreach (var ring in rings)
        {
            AppendRingPath(ring);
        }

        body.Append("\" fill-rule=\"evenodd\"");
        AppendPaint("fill", color);
        body.Append("/>\n");
    }

    public void StrokePath(IReadOnlyList<(double X, double Y)> points, double width, Rgba color)
    {
        // A single point (or empty chain) has no segment to stroke — matches the raster surface,
        // which draws nothing for a sub-two-point path.
        if (points.Count < 2)
        {
            return;
        }

        // Drop sub-tolerance vertices in pixel space (endpoints kept, so the count stays >= 2) to
        // keep the emitted polyline compact. No-op when simplification is off.
        if (simplifyTolerance > 0)
        {
            points = PixelSimplifier.Simplify(points, simplifyTolerance);
        }

        body.Append("<polyline points=\"");
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                body.Append(' ');
            }

            body.Append(Format(points[i].X)).Append(',').Append(Format(points[i].Y));
        }

        body.Append("\" fill=\"none\"");
        AppendPaint("stroke", color);
        body.Append(" stroke-width=\"").Append(Format(width)).Append('"');
        body.Append(" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
    }

    public void FillDisc(double cx, double cy, double radius, Rgba color)
    {
        body.Append("<circle cx=\"").Append(Format(cx))
            .Append("\" cy=\"").Append(Format(cy))
            .Append("\" r=\"").Append(Format(radius)).Append('"');
        AppendPaint("fill", color);
        body.Append("/>\n");
    }

    public void FillRect(double x0, double y0, double x1, double y1, Rgba color)
    {
        body.Append("<rect x=\"").Append(Format(x0))
            .Append("\" y=\"").Append(Format(y0))
            .Append("\" width=\"").Append(Format(x1 - x0))
            .Append("\" height=\"").Append(Format(y1 - y0)).Append('"');
        AppendPaint("fill", color);
        body.Append("/>\n");
    }

    public void DrawText(string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo)
    {
        body.Append("<text x=\"").Append(Format(leftX))
            .Append("\" y=\"").Append(Format(baselineY))
            .Append("\" font-family=\"sans-serif\" font-size=\"").Append(Format(size)).Append('"');
        if (halo is { } haloColor)
        {
            // Outline the glyphs in the halo colour, painted under the fill via paint-order so the
            // text reads against busy fills — the vector analogue of the raster halo ring. The
            // outline weight scales with the label size so it stays proportional across zooms.
            AppendPaint("stroke", haloColor);
            body.Append(" stroke-width=\"").Append(Format(size / 6)).Append('"');
            body.Append(" stroke-linejoin=\"round\" paint-order=\"stroke\"");
        }

        AppendPaint("fill", color);
        body.Append('>').Append(Escape(text)).Append("</text>\n");
    }

    /// <summary>Writes the assembled SVG document to <paramref name="stream"/> as UTF-8.</summary>
    public void WriteTo(Stream stream)
    {
        var bytes = Encoding.UTF8.GetBytes(ToText());
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>The assembled SVG document as a string.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n");
        builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Width)
            .Append("\" height=\"").Append(Height)
            .Append("\" viewBox=\"0 0 ").Append(Width).Append(' ').Append(Height).Append("\">\n");
        // The background is the first painted element so every feature layer renders over it. A
        // fully transparent background is left out entirely so the SVG composites onto whatever
        // sits behind it.
        if (background.A != 0)
        {
            builder.Append("<rect width=\"").Append(Width).Append("\" height=\"").Append(Height).Append('"');
            builder.Append(" fill=\"").Append(HexColor(background)).Append('"');
            if (background.A != 255)
            {
                builder.Append(" fill-opacity=\"").Append(Opacity(background)).Append('"');
            }

            builder.Append("/>\n");
        }

        builder.Append(body);
        builder.Append("</svg>\n");
        return builder.ToString();
    }

    void AppendRingPath((double X, double Y)[] ring)
    {
        // Thin the ring in pixel space before emitting its path (the Z closure still re-joins the
        // surviving first vertex). No-op when simplification is off.
        var points = simplifyTolerance > 0 ? PixelSimplifier.Simplify(ring, simplifyTolerance) : ring;
        for (var i = 0; i < points.Length; i++)
        {
            body.Append(i == 0 ? 'M' : 'L')
                .Append(Format(points[i].X)).Append(',').Append(Format(points[i].Y));
        }

        body.Append('Z');
    }

    // Emits a paint attribute (fill/stroke) plus its matching *-opacity only when the colour is
    // translucent — keeping the common opaque case to a single attribute.
    void AppendPaint(string attribute, Rgba color)
    {
        body.Append(' ').Append(attribute).Append("=\"").Append(HexColor(color)).Append('"');
        if (color.A != 255)
        {
            body.Append(' ').Append(attribute).Append("-opacity=\"").Append(Opacity(color)).Append('"');
        }
    }

    static string HexColor(Rgba color) =>
        $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    static string Opacity(Rgba color) =>
        (color.A / 255d).ToString("0.###", CultureInfo.InvariantCulture);

    static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
