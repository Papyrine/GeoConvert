using System.IO.Compression;

namespace GeoConvert.Web.Services;

/// <summary>
/// The dropdown choice lists shared by the per-format option editors. Kept in one place so the
/// editors and their snapshot tests draw from the same source.
/// </summary>
public static class ExportOptionChoices
{
    public static readonly (MapProjection Value, string Label)[] Projections =
    [
        (MapProjection.Auto, "Automatic"),
        (MapProjection.PlateCarree, "Plate Carrée"),
        (MapProjection.WebMercator, "Web Mercator"),
        (MapProjection.Lambert, "Lambert Conformal Conic"),
        (MapProjection.Goode, "Goode Homolosine"),
    ];

    public static readonly (int Value, string Label)[] Dimensions =
    [
        (512, "512 px"),
        (1024, "1024 px"),
        (2048, "2048 px"),
        (4096, "4096 px"),
        (8192, "8192 px"),
    ];

    public static readonly (CompressionLevel Value, string Label)[] CompressionLevels =
    [
        (CompressionLevel.Optimal, "Optimal"),
        (CompressionLevel.SmallestSize, "Smallest size"),
        (CompressionLevel.Fastest, "Fastest"),
        (CompressionLevel.NoCompression, "None"),
    ];

    public static readonly (ParquetCompression Value, string Label)[] ParquetCodecs =
    [
        (ParquetCompression.Snappy, "Snappy"),
        (ParquetCompression.Gzip, "GZIP"),
        (ParquetCompression.Uncompressed, "Uncompressed"),
    ];
}
