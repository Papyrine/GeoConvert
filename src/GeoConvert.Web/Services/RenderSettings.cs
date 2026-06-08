using System.IO.Compression;

namespace GeoConvert.Web.Services;

/// <summary>
/// User-tunable knobs for the PNG/SVG image export, surfaced in the converter's image-options panel.
/// Mapped to a <see cref="RenderOptions"/> by <see cref="ConversionService.RenderPng"/> /
/// <see cref="ConversionService.RenderSvg"/>. Defaults mirror <see cref="RenderOptions"/>'s own
/// defaults — with the web app's ocean fill and sub-pixel feature culling pre-enabled — so an
/// untouched instance renders exactly as the app did before these controls existed.
/// </summary>
public sealed class RenderSettings
{
    // Size & layout.
    public MapProjection Projection { get; set; } = MapProjection.Auto;
    public int MaxDimension { get; set; } = 2048;
    public int Padding { get; set; } = 8;

    // Strokes & features.
    public int StrokeWidth { get; set; } = 2;
    public int PointRadius { get; set; } = 4;
    public bool StrokeAutoScale { get; set; } = true;
    public double MinFeaturePixels { get; set; } = 1;

    // Labels.
    public bool Labels { get; set; }
    public double LabelSize { get; set; } = 14;

    // Colors. The opaque ones are a single picker; the alpha-carrying ones (fill, ocean, halo) pair a
    // picker with an opacity slider, because <input type="color"> has no alpha channel.
    public Rgba Background { get; set; } = Rgba.White;
    public bool OceanEnabled { get; set; } = true;
    public Rgba Ocean { get; set; } = new(200, 220, 240);
    public Rgba Stroke { get; set; } = new(30, 30, 30);
    public Rgba Fill { get; set; } = new(70, 130, 180, 120);
    public Rgba LabelColor { get; set; } = new(20, 20, 20);
    public bool HaloEnabled { get; set; } = true;
    public Rgba LabelHalo { get; set; } = new(255, 255, 255, 200);

    // Format-specific. PngCompression only affects a PNG download; SvgSimplifyTolerance only an SVG one.
    public CompressionLevel PngCompression { get; set; } = CompressionLevel.Optimal;
    public double SvgSimplifyTolerance { get; set; }

    // --- adapters for the HTML color (#rrggbb, no alpha) and range (0–255 opacity) inputs ---

    public string BackgroundHex { get => ToHex(Background); set => Background = FromHex(value, Background.A); }
    public string OceanHex { get => ToHex(Ocean); set => Ocean = FromHex(value, Ocean.A); }
    public int OceanOpacity { get => Ocean.A; set => Ocean = Ocean with { A = ToByte(value) }; }
    public string StrokeHex { get => ToHex(Stroke); set => Stroke = FromHex(value, Stroke.A); }
    public string FillHex { get => ToHex(Fill); set => Fill = FromHex(value, Fill.A); }
    public int FillOpacity { get => Fill.A; set => Fill = Fill with { A = ToByte(value) }; }
    public string LabelColorHex { get => ToHex(LabelColor); set => LabelColor = FromHex(value, LabelColor.A); }
    public string LabelHaloHex { get => ToHex(LabelHalo); set => LabelHalo = FromHex(value, LabelHalo.A); }
    public int LabelHaloOpacity { get => LabelHalo.A; set => LabelHalo = LabelHalo with { A = ToByte(value) }; }

    static byte ToByte(int value) => (byte) Math.Clamp(value, 0, 255);

    // "#rrggbb" — the only form <input type="color"> accepts. Alpha lives on the Rgba and is preserved
    // across hex edits via the slider-backed *Opacity properties.
    static string ToHex(Rgba color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    static Rgba FromHex(string hex, byte alpha)
    {
        var span = hex.AsSpan().TrimStart('#');
        if (span.Length != 6 ||
            !byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return new(0, 0, 0, alpha);
        }

        return new(r, g, b, alpha);
    }
}
