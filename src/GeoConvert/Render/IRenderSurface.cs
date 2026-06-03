/// <summary>
/// The drawing sink the renderer paints into. The geometry, ocean and label passes (in
/// <see cref="MapRenderer"/>) are written once against this interface so the raster
/// (<see cref="Canvas"/> → PNG) and vector (<c>SvgSurface</c> → SVG) outputs share the same
/// projection, per-layer styling, stroke auto-scaling and greedy label placement — only the
/// primitive sink differs.
/// <para>
/// Coordinates are in canvas pixel space (X right, Y down). <see cref="StrokePath"/> takes a whole
/// polyline rather than individual segments so a vector surface can emit one element per chain while
/// the raster surface just loops its per-segment stroke; the visual result is identical either way.
/// </para>
/// </summary>
interface IRenderSurface
{
    int Width { get; }

    int Height { get; }

    /// <summary>Fills the region bounded by the given rings using the even-odd rule (so holes are excluded).</summary>
    void FillPolygon((double X, double Y)[][] rings, Rgba color);

    /// <summary>Strokes a polyline through the given points. A chain of fewer than two points draws nothing.</summary>
    void StrokePath(IReadOnlyList<(double X, double Y)> points, double width, Rgba color);

    /// <summary>Fills a disc centred at (<paramref name="cx"/>, <paramref name="cy"/>) — used for point markers.</summary>
    void FillDisc(double cx, double cy, double radius, Rgba color);

    /// <summary>Fills an axis-aligned rectangle — used for the label "knockout" backdrop.</summary>
    void FillRect(double x0, double y0, double x1, double y1, Rgba color);

    /// <summary>
    /// Draws a single line of text with its left edge at <paramref name="leftX"/> and baseline at
    /// <paramref name="baselineY"/>, at the given cap-height <paramref name="size"/>. When
    /// <paramref name="halo"/> is non-null the glyphs are outlined in that colour for legibility.
    /// </summary>
    void DrawText(string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo);
}
