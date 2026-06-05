using GeoConvert.ImageSharp;
using GeoConvert.Skia;
using SixLabors.ImageSharp.PixelFormats;
using SixImage = SixLabors.ImageSharp.Image;

// Covers the MapRenderer.PaintSurface seam (via a recording IRenderSurface) and exercises the two
// alternative PNG backends end to end. Backend output is decoded with ImageSharp — which reads any
// PNG colour type — rather than snapshotted, since native rasterizer output isn't byte-stable across
// library versions or platforms; the assertions check size and that geometry actually painted.
public class RenderBackendTests
{
    static readonly Func<FeatureCollection, RenderOptions, byte[]> skia = SkiaRenderer.RenderPng;
    static readonly Func<FeatureCollection, RenderOptions, byte[]> imageSharp = ImageSharpRenderer.RenderPng;

    [Test]
    public async Task PaintSurface_runs_the_shared_pipeline()
    {
        // The extension seam the satellite backends build on: it must validate, project, hand the
        // factory the projected canvas size, and run the geometry pass into the supplied surface.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
            new Feature(new Point(5, 5)),
        };

        var surface = MapRenderer.PaintSurface(
            [features],
            new()
            {
                Bounds = new Envelope(0, 0, 10, 10),
                Width = 64,
                Height = 64,
            },
            (width, height) => new RecordingSurface(width, height));

        await Assert.That(surface.Width).IsEqualTo(64);
        await Assert.That(surface.Height).IsEqualTo(64);
        await Assert.That(surface.Fills).IsGreaterThan(0);
        await Assert.That(surface.Discs).IsGreaterThan(0);
        await Assert.That(surface.Strokes).IsGreaterThan(0);
    }

    [Test]
    public async Task PaintSurface_validates_before_invoking_the_factory()
    {
        // Validation (empty collection) must throw before the factory runs, so a backend never gets
        // handed a degenerate size.
        var invoked = false;
        var threw = false;
        try
        {
            MapRenderer.PaintSurface(
                [new FeatureCollection()],
                new(),
                (width, height) =>
                {
                    invoked = true;
                    return new RecordingSurface(width, height);
                });
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Renders_the_requested_size(string backend)
    {
        var png = Render(backend, Sample.Polygons(), new()
        {
            Width = 200,
            Height = 150,
        });

        var (width, height, _) = Inspect(png);
        await Assert.That(width).IsEqualTo(200);
        await Assert.That(height).IsEqualTo(150);
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Draws_geometry_over_the_background(string backend)
    {
        var png = Render(backend, Sample.Polygons(), new()
        {
            Width = 200,
            Height = 150,
        });

        var (_, _, nonBackground) = Inspect(png);
        await Assert.That(nonBackground).IsGreaterThan(0);
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Clips_to_the_bounding_box(string backend)
    {
        // A bounding box far from the data leaves the canvas empty — same contract as the built-in.
        var png = Render(backend, Sample.Polygons(), new()
        {
            Bounds = new Envelope(1000, 1000, 1001, 1001),
            Width = 64,
            Height = 64,
        });

        var (_, _, nonBackground) = Inspect(png);
        await Assert.That(nonBackground).IsEqualTo(0);
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Renders_all_geometry_types_with_labels(string backend)
    {
        // Every geometry kind plus a polygon hole and a label pass — exercises FillPolygon (even-odd),
        // StrokePath, FillDisc and DrawText (native font + halo) on the backend surface.
        var features = new FeatureCollection
        {
            new Feature(new Point(1, 1), Named("point")),
            new Feature(new MultiPoint([new(2, 2), new(3, 3)]), Named("multipoint")),
            new Feature(new LineString([new(0, 0), new(4, 4)]), Named("line")),
            new Feature(new MultiLineString([new([new(0, 4), new(4, 0)])]), Named("multiline")),
            new Feature(
                new Polygon(
                [
                    [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
                    [new(1, 1), new(2, 1), new(2, 2), new(1, 2), new(1, 1)],
                ]),
                Named("polygon")),
            new Feature(new MultiPolygon([new([[new(1, 1), new(2, 1), new(2, 2), new(1, 1)]])]), Named("multipolygon")),
            new Feature(new GeometryCollection([new Point(2, 3)]), Named("collection")),
        };

        var png = Render(backend, features, new()
        {
            Width = 256,
            Height = 256,
            Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
        });

        var (width, height, nonBackground) = Inspect(png);
        await Assert.That(width).IsEqualTo(256);
        await Assert.That(height).IsEqualTo(256);
        await Assert.That(nonBackground).IsGreaterThan(0);
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Path_overload_writes_a_png(string backend)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "out.png");

        if (backend == "skia")
        {
            SkiaRenderer.RenderPng(Sample.Polygons(), path, new() { Width = 128, Height = 128 });
        }
        else
        {
            ImageSharpRenderer.RenderPng(Sample.Polygons(), path, new() { Width = 128, Height = 128 });
        }

        await Assert.That(File.Exists(path)).IsTrue();
        var (width, _, _) = Inspect(await File.ReadAllBytesAsync(path));
        await Assert.That(width).IsEqualTo(128);
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Path_overload_leaves_no_file_when_render_throws(string backend)
    {
        // Mirrors MapRenderer: validation runs before the destination is touched, so an empty
        // collection leaves no 0-byte file behind.
        using var path = new TempFile();
        var threw = false;
        try
        {
            if (backend == "skia")
            {
                SkiaRenderer.RenderPng(new FeatureCollection(), path);
            }
            else
            {
                ImageSharpRenderer.RenderPng(new FeatureCollection(), path);
            }
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public Task Render_snapshot(string backend)
    {
        // A snapshot of the actual rendered image per backend — fills (with a hole), strokes, point
        // markers and labels. PNG snapshots compare via SSIM (see ModuleInitializer), so minor
        // antialiasing differences between rasterizer versions don't trip the baseline while a real
        // visual regression still does. Pinned to PlateCarree for a stable, pixel-aligned layout.
        var features = new FeatureCollection
        {
            new Feature(
                new Polygon(
                [
                    [new(0, 0), new(10, 0), new(10, 8), new(0, 8), new(0, 0)],
                    [new(2, 2), new(5, 2), new(5, 5), new(2, 5), new(2, 2)],
                ]),
                Named("Region")),
            new Feature(new LineString([new(1, 1), new(9, 7), new(1, 7), new(9, 1)]), Named("Route")),
            new Feature(new MultiPoint([new(3, 6), new(7, 3)]), Named("City")),
        };

        var png = Render(backend, features, new()
        {
            Bounds = new Envelope(-1, -1, 11, 9),
            Width = 300,
            Height = 220,
            Projection = MapProjection.PlateCarree,
            Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
        });

        return Verify(png, "png");
    }

    [Test]
    [Arguments("skia")]
    [Arguments("imagesharp")]
    public async Task Stacks_multiple_collections_in_order(string backend)
    {
        // The list overload stacks top-level layers; union bounds cover both. Confirms the multi-FC
        // path reaches the backend surface and paints both inputs.
        var west = new FeatureCollection
        {
            new Feature(new Point(-40, 0)),
        };
        var east = new FeatureCollection
        {
            new Feature(new Point(40, 0)),
        };

        var png = backend == "skia"
            ? SkiaRenderer.RenderPng([west, east], new() { Width = 200, Height = 60, PointRadius = 4 })
            : ImageSharpRenderer.RenderPng([west, east], new() { Width = 200, Height = 60, PointRadius = 4 });

        var (_, _, nonBackground) = Inspect(png);
        await Assert.That(nonBackground).IsGreaterThan(0);
    }

    static byte[] Render(string backend, FeatureCollection features, RenderOptions options) =>
        (backend == "skia" ? skia : imageSharp)(features, options);

    static Dictionary<string, object?> Named(string name) =>
        new()
        {
            ["name"] = name,
        };

    // Decodes a PNG from any backend (built-in, Skia or ImageSharp) and counts non-white pixels.
    static (int Width, int Height, int NonBackground) Inspect(byte[] png)
    {
        using var image = SixImage.Load<Rgba32>(png);
        var nonBackground = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                if (pixel.R != 255 || pixel.G != 255 || pixel.B != 255)
                {
                    nonBackground++;
                }
            }
        }

        return (image.Width, image.Height, nonBackground);
    }

    // A counting IRenderSurface used to prove the PaintSurface seam drives the shared geometry pass
    // without needing a real rasterizer.
    sealed class RecordingSurface(int surfaceWidth, int surfaceHeight) :
        IRenderSurface
    {
        public int Width => surfaceWidth;

        public int Height => surfaceHeight;

        public int Fills { get; private set; }

        public int Strokes { get; private set; }

        public int Discs { get; private set; }

        public int Rects { get; private set; }

        public int Texts { get; private set; }

        public void FillPolygon((double X, double Y)[][] rings, Rgba color) =>
            Fills++;

        public void StrokePath(IReadOnlyList<(double X, double Y)> points, double strokeWidth, Rgba color) =>
            Strokes++;

        public void FillDisc(double cx, double cy, double radius, Rgba color) =>
            Discs++;

        public void FillRect(double x0, double y0, double x1, double y1, Rgba color) =>
            Rects++;

        public void DrawText(string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo) =>
            Texts++;
    }
}
