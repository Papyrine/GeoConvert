namespace GeoConvert.App;

/// <summary>Options for a KMZ write — the zip deflate level for the archived <c>doc.kml</c> entry.</summary>
public sealed class KmzSettings
{
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
