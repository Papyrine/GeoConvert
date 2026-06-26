namespace GeoConvert.App;

/// <summary>
/// The dropdown choice lists shared by the GUI option editors and the CLI help — the desktop
/// counterpart of the Blazor app's <c>ExportOptionChoices</c>, kept in one place so labels stay
/// consistent across the window, the diff view and the command line.
/// </summary>
public static class OptionChoices
{
    public static readonly (MapProjection Value, string Label)[] Projections =
    [
        (MapProjection.Auto, "Automatic"),
        (MapProjection.PlateCarree, "Plate Carrée"),
        (MapProjection.WebMercator, "Web Mercator"),
        (MapProjection.Lambert, "Lambert Conformal Conic"),
        (MapProjection.Goode, "Goode Homolosine"),
    ];

    public static readonly (RendererBackend Value, string Label)[] Renderers =
    [
        (RendererBackend.BuiltIn, "Built-in (dependency-free)"),
        (RendererBackend.ImageSharp, "ImageSharp"),
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
