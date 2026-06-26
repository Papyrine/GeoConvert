namespace GeoConvert.App;

/// <summary>Bridges GeoConvert's <see cref="Rgba"/> and WinForms' <see cref="Color"/>.</summary>
public static class RgbaColors
{
    public static Color ToColor(this Rgba color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    public static Rgba ToRgba(this Color color) =>
        new(color.R, color.G, color.B, color.A);

    /// <summary>Replaces only the RGB channels, preserving the existing alpha — what a WinForms
    /// <see cref="ColorDialog"/> (which has no alpha channel) should do when paired with a separate
    /// opacity slider.</summary>
    public static Rgba WithRgbOf(this Rgba color, Color picked) =>
        new(picked.R, picked.G, picked.B, color.A);
}
