using System.IO.Compression;

namespace GeoConvert.Web.Services;

/// <summary>
/// Options for a GeoParquet download — the data-page <see cref="ParquetCompression"/> codec, plus the
/// deflate level used only when the codec is <see cref="ParquetCompression.Gzip"/>.
/// </summary>
public sealed class GeoParquetSettings
{
    public ParquetCompression Codec { get; set; } = ParquetCompression.Snappy;
    public CompressionLevel GzipLevel { get; set; } = CompressionLevel.Optimal;
}
