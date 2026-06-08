using System.IO.Compression;

namespace GeoConvert.Web.Services;

/// <summary>Options for a KMZ download — the zip deflate level for the archived <c>doc.kml</c> entry.</summary>
public sealed class KmzSettings
{
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
