namespace GeoConvert.App;

static class Images
{
    /// <summary>
    /// Fully materialises PNG bytes into a <see cref="Bitmap"/> so the backing stream can be disposed
    /// immediately — a Bitmap built straight from a stream keeps a lazy reference to it.
    /// </summary>
    public static Bitmap DecodePng(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var decoded = new Bitmap(stream);
        return new(decoded);
    }
}
