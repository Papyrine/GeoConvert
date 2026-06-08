public class SvgTests
{
    [Test]
    public async Task Emits_svg_root_with_requested_size()
    {
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 200,
            Height = 150,
        });

        await Assert.That(svg.StartsWith("<?xml", StringComparison.Ordinal)).IsTrue();
        await Assert.That(svg).Contains("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"150\" viewBox=\"0 0 200 150\">");
        await Assert.That(svg.EndsWith("</svg>\n", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Draws_geometry_elements()
    {
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 200,
            Height = 150,
        });

        // Polygons fill (a <path>) and stroke (a <polyline>).
        await Assert.That(svg).Contains("<path ");
        await Assert.That(svg).Contains("<polyline ");
    }

    [Test]
    public async Task Renders_all_geometry_types()
    {
        var features = new FeatureCollection
        {
            new Feature(new Point(1, 1)),
            new Feature(new MultiPoint([new(2, 2), new(3, 3)])),
            new Feature(new LineString([new(0, 0), new(4, 4)])),
            new Feature(new MultiLineString([new([new(0, 4), new(4, 0)])])),
            new Feature(new Polygon([[new(0, 0), new(4, 0), new(4, 4), new(0, 0)]])),
            new Feature(new MultiPolygon([new([[new(1, 1), new(2, 1), new(2, 2), new(1, 1)]])])),
            new Feature(new GeometryCollection([new Point(2, 3)])),
        };

        var svg = MapRenderer.RenderSvg(
            features,
            new()
            {
                Width = 128,
                Height = 128,
            });

        await Assert.That(svg).Contains("<circle ");
        await Assert.That(svg).Contains("<polyline ");
        await Assert.That(svg).Contains("<path ");
    }

    [Test]
    public async Task Transparent_fill_emits_no_path()
    {
        // A fully transparent fill paints nothing, so FillPolygon skips the <path> — the polygon's
        // stroke (<polyline>) still renders.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            Fill = Rgba.Transparent,
        });

        await Assert.That(svg.Contains("<path ")).IsFalse();
        await Assert.That(svg).Contains("<polyline ");
    }

    [Test]
    public async Task Single_vertex_line_emits_no_polyline()
    {
        // A one-vertex LineString has no segment to stroke, so StrokePath emits nothing.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(5, 5)])),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            // Pin to PlateCarree so PrepareLine yields the single-point path as-is.
            Projection = MapProjection.PlateCarree,
        });

        await Assert.That(svg.Contains("<polyline ")).IsFalse();
    }

    [Test]
    public async Task Opaque_background_emits_filled_rect()
    {
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 64,
            Height = 64,
            Background = new(250, 240, 230),
        });

        await Assert.That(svg).Contains("<rect width=\"64\" height=\"64\" fill=\"#faf0e6\"/>");
    }

    [Test]
    public async Task Translucent_background_emits_fill_opacity()
    {
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 64,
            Height = 64,
            Background = new(10, 20, 30, 128),
        });

        await Assert.That(svg).Contains("<rect width=\"64\" height=\"64\" fill=\"#0a141e\" fill-opacity=\"0.502\"/>");
    }

    [Test]
    public async Task Transparent_background_emits_no_rect()
    {
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 64,
            Height = 64,
            Background = Rgba.Transparent,
        });

        await Assert.That(svg.Contains("<rect", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Translucent_fill_emits_fill_opacity()
    {
        // The default Fill is semi-transparent — exercises the AppendPaint *-opacity branch.
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 64,
            Height = 64,
        });

        await Assert.That(svg).Contains("fill-opacity=");
        // The default Stroke is opaque, so its paint attribute carries no opacity.
        await Assert.That(svg).Contains("stroke=\"#1e1e1e\" stroke-width");
    }

    [Test]
    public async Task Renders_labels_as_text_with_halo()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]]), Props("name", "Block")),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 256,
            Height = 256,
            Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
        });

        await Assert.That(svg).Contains("<text ");
        await Assert.That(svg).Contains(">Block</text>");
        // The default halo is non-null, so the text carries a stroke outline via paint-order.
        await Assert.That(svg).Contains("paint-order=\"stroke\"");
    }

    [Test]
    public async Task Renders_labels_without_halo()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]]), Props("name", "Block")),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 256,
            Height = 256,
            Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
            LabelHalo = null,
        });

        await Assert.That(svg).Contains("<text ");
        await Assert.That(svg.Contains("paint-order=\"stroke\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Renders_label_knockout_rect()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]]), Props("name", "Block")),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 256,
            Height = 256,
            Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
            LabelKnockout = new(255, 255, 255),
        });

        // The knockout backdrop is a <rect> painted under the label text.
        await Assert.That(svg).Contains("<rect x=");
        await Assert.That(svg).Contains("<text ");
    }

    [Test]
    public async Task Escapes_special_characters_in_labels()
    {
        var features = new FeatureCollection
        {
            new Feature(new Point(5, 5), Props("name", "A & B <C>")),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 256,
            Height = 256,
            Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
        });

        await Assert.That(svg).Contains("A &amp; B &lt;C&gt;");
    }

    [Test]
    public async Task Ocean_fills_world_envelope()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)]])),
        };

        var svg = MapRenderer.RenderSvg(features, new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 100,
            Height = 100,
            Projection = MapProjection.PlateCarree,
            Ocean = new(100, 150, 200),
        });

        // The ocean pass paints the envelope as a filled path before the features.
        await Assert.That(svg).Contains("fill=\"#6496c8\"");
    }

    [Test]
    public async Task Multiple_collections_render_in_order()
    {
        var lower = new FeatureCollection
        {
            Name = "lower",
            Features =
            {
                new(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
            },
        };
        var upper = new FeatureCollection
        {
            Name = "upper",
            Features =
            {
                new(new Polygon([[new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)]])),
            },
        };

        var svg = MapRenderer.RenderSvg([lower, upper], new()
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            Projection = MapProjection.PlateCarree,
            LayerStyle = layer => layer.Name == "upper"
                ? new() { Fill = new(220, 30, 30) }
                : new() { Fill = new(20, 200, 20) },
        });

        var lowerIndex = svg.IndexOf("#14c814", StringComparison.Ordinal);
        var upperIndex = svg.IndexOf("#dc1e1e", StringComparison.Ordinal);
        await Assert.That(lowerIndex).IsGreaterThan(0);
        // The lower layer paints first, so its fill appears earlier in the document.
        await Assert.That(lowerIndex).IsLessThan(upperIndex);
    }

    [Test]
    public async Task Empty_collection_throws() =>
        await Assert.That(TestSupport.ThrowsGeo(() => MapRenderer.RenderSvg(new FeatureCollection()))).IsTrue();

    [Test]
    public async Task Reading_svg_throws()
    {
        using var stream = new MemoryStream();
        await Assert.That(TestSupport.ThrowsGeo(() => GeoConverter.Read(stream, GeoFormat.Svg))).IsTrue();
    }

    [Test]
    public async Task Path_overload_leaves_no_file_when_render_throws()
    {
        using var path = new TempFile();
        await Assert.That(TestSupport.ThrowsGeo(() => MapRenderer.RenderSvg(new FeatureCollection(), path))).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Writes_to_path_and_stream()
    {
        using var path = new TempFile();
        MapRenderer.RenderSvg(Sample.Polygons(), path, new() { Width = 64, Height = 64 });
        var fromPath = await File.ReadAllTextAsync(path);
        await Assert.That(fromPath.StartsWith("<?xml", StringComparison.Ordinal)).IsTrue();

        using var stream = new MemoryStream();
        MapRenderer.RenderSvg(Sample.Polygons(), stream, new RenderOptions { Width = 64, Height = 64 });
        await Assert.That(Encoding.UTF8.GetString(stream.ToArray()).StartsWith("<?xml", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Converts_geojson_to_svg_via_facade()
    {
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.svg");

        GeoConverter.Convert(input, output);

        await Assert.That(File.Exists(output)).IsTrue();
        await Assert.That((await File.ReadAllTextAsync(output)).StartsWith("<?xml", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Reports_progress_through_render_options()
    {
        // Exercises the RenderOptions.Progress (RenderSvgWithProgress sink) path.
        var svg = MapRenderer.RenderSvg(Sample.Polygons(), new()
        {
            Width = 64,
            Height = 64,
            Progress = new Progress<ConvertProgress>(_ => { }),
        });

        await Assert.That(svg).Contains("<svg ");
    }

    [Test]
    public async Task Converts_with_progress_via_facade()
    {
        // Drives the facade's Svg write branch with a non-null progress sink (the internal
        // RenderSvg(features, stream, progress) entry point).
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.svg");

        var progress = new Progress<ConvertProgress>(_ => { });
        GeoConverter.Convert(input, output, progress);

        await Assert.That(File.Exists(output)).IsTrue();
    }

    [Test]
    public Task Render_snapshot()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(10, 0), new(10, 8), new(0, 8), new(0, 0)],
                [new(2, 2), new(5, 2), new(5, 5), new(2, 5), new(2, 2)],
            ])),
            new Feature(new LineString([new(1, 1), new(9, 7), new(1, 7), new(9, 1)])),
            new Feature(new MultiPoint([new(3, 6), new(7, 3), new(5, 5)])),
        };

        var svg = MapRenderer.RenderSvg(
            features,
            new()
            {
                Bounds = new Envelope(-1, -1, 11, 9),
                Width = 300,
                Height = 220,
                Projection = MapProjection.PlateCarree,
            });

        return Verify(svg, "svg");
    }

    [Test]
    public async Task Simplify_tolerance_thins_rings_and_polylines()
    {
        // A polygon ring and a line each densely sampled with near-collinear vertices: at sub-pixel
        // tolerance Douglas–Peucker collapses the redundant points, so the simplified SVG is shorter
        // while both element kinds still render.
        var ring = new List<Position>();
        var line = new List<Position>();
        for (var i = 0; i <= 100; i++)
        {
            var x = i / 10.0;
            // A barely-perceptible wobble (well under a pixel once projected) on an otherwise straight edge.
            ring.Add(new(x, 5 + (i % 2) * 0.001));
            line.Add(new(x, 2 + (i % 2) * 0.001));
        }

        ring.Add(new(10, 0));
        ring.Add(new(0, 0));
        ring.Add(ring[0]);

        var features = new FeatureCollection
        {
            new Feature(new Polygon([ring])),
            new Feature(new LineString(line)),
        };

        static RenderOptions Options(double tolerance) =>
            new()
            {
                Bounds = new Envelope(0, 0, 10, 6),
                Width = 200,
                Height = 120,
                Projection = MapProjection.PlateCarree,
                Svg = new()
                {
                    SimplifyTolerance = tolerance
                },
            };

        var full = MapRenderer.RenderSvg(features, Options(0));
        var simplified = MapRenderer.RenderSvg(features, Options(0.5));

        await Assert.That(simplified.Length).IsLessThan(full.Length);
        await Assert.That(simplified).Contains("<path ");
        await Assert.That(simplified).Contains("<polyline ");
    }

    static Dictionary<string, object?> Props(string key, object? value) =>
        new()
        {
            [key] = value
        };
}
