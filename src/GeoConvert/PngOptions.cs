namespace GeoConvert;

/// <summary>
/// PNG-only knobs on <see cref="RenderOptions.Png"/>. These affect the raster encode and are ignored
/// when rendering to SVG.
/// </summary>
public sealed class PngOptions
{
    /// <summary>
    /// Deflate level used for the PNG <c>IDAT</c> chunk. Defaults to <see cref="CompressionLevel.Optimal"/>;
    /// drop to <see cref="CompressionLevel.Fastest"/> for quicker writes or
    /// <see cref="CompressionLevel.SmallestSize"/> when output size matters more than CPU.
    /// </summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
