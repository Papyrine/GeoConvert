namespace GeoConvert;

/// <summary>
/// SVG-only knobs on <see cref="RenderOptions.Svg"/>. These affect the vector output and are ignored
/// when rendering to PNG.
/// </summary>
public sealed class SvgOptions
{
    /// <summary>
    /// Pixel-space vertex reduction applied to the SVG output. When positive, each emitted polygon ring
    /// and polyline is simplified with Douglas–Peucker in canvas pixel space at this tolerance — a vertex
    /// sitting less than this many pixels off the chord between its surviving neighbours is dropped,
    /// always keeping the first and last vertex so closed rings stay closed. Because the threshold is
    /// in output pixels it is resolution-aware: a value below ~1 is visually lossless at the rendered
    /// size while still collapsing the dense sub-pixel detail (detailed coastlines, country borders)
    /// that otherwise bloats a world-scale SVG to hundreds of megabytes. The pass runs after
    /// projection and the <see cref="RenderOptions.MinFeaturePixels"/> selection, so it only thins
    /// geometry that is actually being drawn. Defaults to <c>0</c> (off): every projected vertex is
    /// emitted, matching the 2-decimal-rounded coordinate output.
    /// </summary>
    public double SimplifyTolerance { get; set; }
}
