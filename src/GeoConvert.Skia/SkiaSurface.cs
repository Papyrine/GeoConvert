using System.IO.Compression;
using SkiaSharp;

namespace GeoConvert.Skia;

/// <summary>
/// Skia-backed <see cref="IRenderSurface"/>: the renderer's geometry, ocean and label passes paint
/// into an <see cref="SKBitmap"/> via an <see cref="SKCanvas"/>, then <see cref="Encode"/> writes the
/// result as a PNG through Skia's encoder. Coordinates are canvas pixel space (X right, Y down), the
/// same convention Skia uses, so positions pass straight through. Fills and strokes are antialiased.
/// </summary>
sealed class SkiaSurface : IRenderSurface, IDisposable
{
    readonly SKBitmap bitmap;
    readonly SKCanvas canvas;

    public int Width { get; }

    public int Height { get; }

    public SkiaSurface(int width, int height, Rgba background)
    {
        Width = width;
        Height = height;
        // Unpremultiplied straight-alpha RGBA matches GeoConvert's Rgba and the built-in Canvas, so
        // translucent fills composite the same way and the PNG comes out 8-bit RGBA.
        bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        canvas = new(bitmap);
        canvas.Clear(ToColor(background));
    }

    public void FillPolygon((double X, double Y)[][] rings, Rgba color)
    {
        // A fully transparent fill paints nothing — skip it, matching the other surfaces.
        if (color.A == 0)
        {
            return;
        }

        using var path = new SKPath
        {
            // Even-odd so interior rings (holes) punch through, matching the interface contract.
            FillType = SKPathFillType.EvenOdd,
        };
        foreach (var ring in rings)
        {
            if (ring.Length == 0)
            {
                continue;
            }

            path.MoveTo((float)ring[0].X, (float)ring[0].Y);
            for (var i = 1; i < ring.Length; i++)
            {
                path.LineTo((float)ring[i].X, (float)ring[i].Y);
            }

            path.Close();
        }

        using var paint = Fill(color);
        canvas.DrawPath(path, paint);
    }

    public void StrokePath(IReadOnlyList<(double X, double Y)> points, double width, Rgba color)
    {
        // A single point (or empty chain) has no segment to stroke — matches the other surfaces.
        if (points.Count < 2)
        {
            return;
        }

        using var path = new SKPath();
        path.MoveTo((float)points[0].X, (float)points[0].Y);
        for (var i = 1; i < points.Count; i++)
        {
            path.LineTo((float)points[i].X, (float)points[i].Y);
        }

        using var paint = new SKPaint
        {
            Color = ToColor(color),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawPath(path, paint);
    }

    public void FillDisc(double cx, double cy, double radius, Rgba color)
    {
        using var paint = Fill(color);
        canvas.DrawCircle((float)cx, (float)cy, (float)radius, paint);
    }

    public void FillRect(double x0, double y0, double x1, double y1, Rgba color)
    {
        using var paint = Fill(color);
        canvas.DrawRect((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0), paint);
    }

    public void DrawText(string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo)
    {
        // RenderOptions.LabelSize (the `size` here) is a cap height in pixels, but a native font is
        // sized by its em. A typical sans has a cap height around 0.7 em, so scale up to land the
        // glyphs at roughly the requested height.
        var emSize = (float)(size / 0.7);
        using var font = new SKFont(SKTypeface.Default, emSize);
        if (halo is { } haloColor)
        {
            // Outline the glyphs in the halo colour first so the fill paints over it — the raster
            // analogue of the SVG halo, for legibility against busy fills.
            using var haloPaint = new SKPaint
            {
                Color = ToColor(haloColor),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, emSize / 6f),
                StrokeJoin = SKStrokeJoin.Round,
            };
            canvas.DrawText(text, (float)leftX, (float)baselineY, font, haloPaint);
        }

        using var paint = Fill(color);
        canvas.DrawText(text, (float)leftX, (float)baselineY, font, paint);
    }

    /// <summary>Encodes the painted bitmap as a PNG to <paramref name="stream"/>. Skia chooses its own
    /// deflate settings, so <paramref name="compression"/> is accepted for API symmetry but not
    /// applied (it drives only the built-in encoder).</summary>
    public void Encode(Stream stream, CompressionLevel compression)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(stream);
    }

    static SKPaint Fill(Rgba color) =>
        new()
        {
            Color = ToColor(color),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

    static SKColor ToColor(Rgba color) =>
        new(color.R, color.G, color.B, color.A);

    public void Dispose()
    {
        canvas.Dispose();
        bitmap.Dispose();
    }
}
