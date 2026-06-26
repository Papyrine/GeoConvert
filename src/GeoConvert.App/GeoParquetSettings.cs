namespace GeoConvert.App;

/// <summary>
/// Options for a GeoParquet write — the data-page <see cref="ParquetCompression"/> codec, plus the
/// deflate level used only when the codec is <see cref="ParquetCompression.Gzip"/>.
/// </summary>
public sealed class GeoParquetSettings
{
    public ParquetCompression Codec { get; set; } = ParquetCompression.Snappy;
    public CompressionLevel GzipLevel { get; set; } = CompressionLevel.Optimal;
}
