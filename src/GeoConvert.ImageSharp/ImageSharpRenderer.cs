namespace GeoConvert.ImageSharp;

/// <summary>
/// Renders a <see cref="FeatureCollection"/> to a PNG raster through
/// <see href="https://github.com/SixLabors/ImageSharp">SixLabors.ImageSharp</see>, as an alternative
/// to GeoConvert's built-in dependency-free <see cref="MapRenderer"/>. Every overload reuses the exact
/// same projection, per-layer styling, stroke auto-scaling and label-placement pipeline as
/// <see cref="MapRenderer.RenderPng(FeatureCollection, RenderOptions)"/> — honouring the same
/// <see cref="RenderOptions"/> — and differs only in that ImageSharp does the rasterizing and PNG
/// encoding. Like the built-in renderer this is a write-only export. Labels are drawn with an
/// installed system sans-serif font.
/// </summary>
public static class ImageSharpRenderer
{
    public static byte[] RenderPng(FeatureCollection features, RenderOptions? options = null) =>
        RenderPng([features], options);

    public static void RenderPng(FeatureCollection features, string path, RenderOptions? options = null) =>
        RenderPng([features], path, options);

    public static void RenderPng(FeatureCollection features, Stream stream, RenderOptions? options = null) =>
        RenderPng([features], stream, options);

    /// <summary>
    /// Renders multiple <see cref="FeatureCollection"/>s as stacked top-level layers, in order — the
    /// first paints under, the last on top — the ImageSharp counterpart of
    /// <see cref="MapRenderer.RenderPng(IReadOnlyList{FeatureCollection}, RenderOptions)"/>, with the
    /// same stacking, per-layer styling and union-bounds behaviour.
    /// </summary>
    public static byte[] RenderPng(IReadOnlyList<FeatureCollection> layers, RenderOptions? options = null)
    {
        using var memory = new MemoryStream();
        RenderPng(layers, memory, options);
        return memory.ToArray();
    }

    public static void RenderPng(IReadOnlyList<FeatureCollection> layers, string path, RenderOptions? options = null)
    {
        // Render fully into memory before touching the destination so a validation throw (empty
        // collection, non-positive width) leaves the file untouched rather than stranding a 0-byte
        // PNG — matching MapRenderer's path-overload contract.
        var bytes = RenderPng(layers, options);
        File.WriteAllBytes(path, bytes);
    }

    public static void RenderPng(IReadOnlyList<FeatureCollection> layers, Stream stream, RenderOptions? options = null)
    {
        options ??= new();
        using var surface = MapRenderer.PaintSurface(layers, options, (width, height) => new ImageSharpSurface(width, height, options.Background));
        surface.Encode(stream, options.Compression);
    }
}
