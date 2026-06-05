using System.IO.Compression;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = SixLabors.ImageSharp.Color;

namespace GeoConvert.ImageSharp;

/// <summary>
/// ImageSharp-backed <see cref="IRenderSurface"/>: the renderer's geometry, ocean and label passes
/// paint into an <see cref="Image{Rgba32}"/>, then <see cref="Encode"/> writes the result as a PNG.
/// Coordinates are canvas pixel space (X right, Y down), the same convention ImageSharp uses, so
/// positions pass straight through. Fills, strokes and discs are antialiased.
/// </summary>
sealed class ImageSharpSurface : IRenderSurface, IDisposable
{
    static readonly DrawingOptions evenOdd = new()
    {
        GraphicsOptions = new()
        {
            Antialias = true,
        },
        ShapeOptions = new()
        {
            // Even-odd so interior rings (holes) punch through, matching the interface contract.
            IntersectionRule = IntersectionRule.EvenOdd,
        },
    };

    static readonly DrawingOptions antialiased = new()
    {
        GraphicsOptions = new()
        {
            Antialias = true,
        },
    };

    // Resolved once on first label: picking a system font is comparatively expensive and the family
    // doesn't change between renders.
    static FontFamily? cachedFamily;

    readonly Image<Rgba32> image;

    public int Width { get; }

    public int Height { get; }

    public ImageSharpSurface(int width, int height, Rgba background)
    {
        Width = width;
        Height = height;
        image = new(width, height, ToPixel(background));
    }

    public void FillPolygon((double X, double Y)[][] rings, Rgba color)
    {
        // A fully transparent fill paints nothing — skip it, matching the other surfaces.
        if (color.A == 0)
        {
            return;
        }

        var builder = new PathBuilder();
        foreach (var ring in rings)
        {
            if (ring.Length == 0)
            {
                continue;
            }

            builder.StartFigure();
            builder.AddLines(ToPoints(ring));
            builder.CloseFigure();
        }

        var path = builder.Build();
        image.Mutate(_ => _.Fill(evenOdd, ToColor(color), path));
    }

    public void StrokePath(IReadOnlyList<(double X, double Y)> points, double width, Rgba color)
    {
        // A single point (or empty chain) has no segment to stroke — matches the other surfaces.
        if (points.Count < 2)
        {
            return;
        }

        var vertices = new PointF[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            vertices[i] = new((float)points[i].X, (float)points[i].Y);
        }

        var builder = new PathBuilder();
        builder.AddLines(vertices);
        var path = builder.Build();
        var pen = Pens.Solid(ToColor(color), (float)width);
        image.Mutate(_ => _.Draw(antialiased, pen, path));
    }

    public void FillDisc(double cx, double cy, double radius, Rgba color)
    {
        var disc = new EllipsePolygon((float)cx, (float)cy, (float)radius);
        image.Mutate(_ => _.Fill(antialiased, ToColor(color), disc));
    }

    public void FillRect(double x0, double y0, double x1, double y1, Rgba color)
    {
        var rectangle = new RectangularPolygon((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0));
        image.Mutate(_ => _.Fill(antialiased, ToColor(color), rectangle));
    }

    public void DrawText(string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo)
    {
        // RenderOptions.LabelSize (the `size` here) is a cap height in pixels, but a native font is
        // sized by its em. A typical sans has a cap height around 0.7 em, so scale up to land the
        // glyphs at roughly the requested height. ImageSharp lays text out from the top of the line
        // box, so shift the origin up by an approximate ascent to put the baseline on baselineY.
        var emSize = (float)(size / 0.7);
        var font = ResolveFont(emSize);
        var origin = new PointF((float)leftX, (float)(baselineY - emSize * 0.8));
        var textOptions = new RichTextOptions(font)
        {
            Origin = origin,
        };
        if (halo is { } haloColor)
        {
            // Lay down a fat halo-coloured glyph first (filled and outlined in the halo colour), then
            // paint the foreground fill on top — so the text reads against busy fills. Drawing the
            // halo as a separate underpass (rather than as the same call's outline pen) keeps the
            // foreground colour on top instead of letting the outline eat into it.
            var haloBrush = new SolidBrush(ToColor(haloColor));
            var haloPen = Pens.Solid(ToColor(haloColor), Math.Max(1f, emSize / 6f));
            image.Mutate(_ => _.DrawText(textOptions, text, haloBrush, haloPen));
        }

        image.Mutate(_ => _.DrawText(textOptions, text, ToColor(color)));
    }

    /// <summary>Encodes the painted image as a PNG to <paramref name="stream"/>, mapping
    /// <paramref name="compression"/> onto ImageSharp's deflate level.</summary>
    public void Encode(Stream stream, CompressionLevel compression)
    {
        var encoder = new PngEncoder
        {
            CompressionLevel = ToCompression(compression),
        };
        image.Save(stream, encoder);
    }

    static Font ResolveFont(float emSize)
    {
        cachedFamily ??= PickFamily();
        return cachedFamily.Value.CreateFont(emSize, FontStyle.Regular);
    }

    static FontFamily PickFamily()
    {
        // Prefer a plain sans-serif when one is installed; otherwise take whatever the system offers.
        string[] preferred = ["Arial", "Helvetica", "Liberation Sans", "DejaVu Sans", "Segoe UI", "Verdana", "Tahoma"];
        foreach (var name in preferred)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return family;
            }
        }

        foreach (var family in SystemFonts.Families)
        {
            return family;
        }

        throw new GeoConvertException(
            "No system fonts are available for the ImageSharp renderer to draw labels. Install a font, or render without a Label.");
    }

    static PngCompressionLevel ToCompression(CompressionLevel compression) =>
        compression switch
        {
            CompressionLevel.NoCompression => PngCompressionLevel.Level0,
            CompressionLevel.Fastest => PngCompressionLevel.Level1,
            CompressionLevel.SmallestSize => PngCompressionLevel.Level9,
            _ => PngCompressionLevel.Level6,
        };

    static PointF[] ToPoints((double X, double Y)[] ring)
    {
        var points = new PointF[ring.Length];
        for (var i = 0; i < ring.Length; i++)
        {
            points[i] = new((float)ring[i].X, (float)ring[i].Y);
        }

        return points;
    }

    static Color ToColor(Rgba color) =>
        Color.FromPixel(ToPixel(color));

    static Rgba32 ToPixel(Rgba color) =>
        new(color.R, color.G, color.B, color.A);

    public void Dispose() =>
        image.Dispose();
}
