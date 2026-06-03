public class ConversionServiceTests
{
    [Test]
    public async Task DetectReadable_KnownExtension_ReturnsFormat()
    {
        var info = ConversionService.DetectReadable("world.geojson");

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Format).IsEqualTo(GeoFormat.GeoJson);
    }

    // Shapefile is path-based (spans .shp/.shx/.dbf) so it can't be read from a single browser upload.
    [Test]
    public async Task DetectReadable_Shapefile_ReturnsNull() =>
        await Assert.That(ConversionService.DetectReadable("world.shp")).IsNull();

    [Test]
    public async Task DetectReadable_UnknownExtension_ReturnsNull() =>
        await Assert.That(ConversionService.DetectReadable("notes.txt")).IsNull();

    [Test]
    public async Task ReadableFormats_ExcludePngAndShapefile()
    {
        var formats = ConversionService.ReadableFormats.Select(_ => _.Format).ToList();

        await Assert.That(formats).DoesNotContain(GeoFormat.Png);
        await Assert.That(formats).DoesNotContain(GeoFormat.Shapefile);
        await Assert.That(formats).Contains(GeoFormat.GeoJson);
    }

    [Test]
    public async Task WritableFormats_IncludePngExcludeShapefile()
    {
        var formats = ConversionService.WritableFormats.Select(_ => _.Format).ToList();

        await Assert.That(formats).Contains(GeoFormat.Png);
        await Assert.That(formats).DoesNotContain(GeoFormat.Shapefile);
    }

    [Test]
    public async Task ReadableAccept_OffersOnlyDetectableReadableExtensions()
    {
        var accept = ConversionService.ReadableAccept.Split(',');

        // Every offered extension must actually resolve to a readable format (guards typos/drift).
        foreach (var extension in accept)
        {
            await Assert.That(ConversionService.DetectReadable($"map{extension}")).IsNotNull();
        }

        // Every readable format's canonical extension must be offered.
        foreach (var format in ConversionService.ReadableFormats)
        {
            await Assert.That(accept).Contains(format.Extension);
        }

        // The detection aliases are included so those files aren't filtered out.
        await Assert.That(accept).Contains(".json");
        await Assert.That(accept).Contains(".geoparquet");
    }

    [Test]
    public async Task Read_CountsFeatures()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);

        await Assert.That(features.Count).IsEqualTo(2);
    }

    [Test]
    public Task Convert_GeoJsonToKml() =>
        Verify(ToText(ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.Kml)));

    [Test]
    public Task Convert_GeoJsonToGpx() =>
        Verify(ToText(ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.Gpx)));

    [Test]
    public async Task Convert_ToPng_ProducesPngSignature()
    {
        var png = ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.Png);

        // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
        await Assert.That(png.Length).IsGreaterThan(8);
        await Assert.That(png[..8]).IsEquivalentTo(new byte[] {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A});
    }

    [Test]
    public async Task RenderPng_WithMaxDimension_CapsLongerEdge()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);

        var png = ConversionService.RenderPng(features, MapProjection.PlateCarree, 256);

        // PNG IHDR width/height are big-endian 32-bit ints at byte offsets 16 and 20.
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        await Assert.That(Math.Max(width, height)).IsEqualTo(256);
    }

    [Test]
    public async Task RenderPng_WithoutMaxDimension_UsesDefaultSize()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);

        var png = ConversionService.RenderPng(features, MapProjection.Auto, 0);

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        await Assert.That(width).IsEqualTo(2048);
    }

    [Test]
    public async Task RenderPng_FiltersSubPixelFeatures()
    {
        // Pins that ConversionService.RenderPng applies RenderOptions.MinFeaturePixels = 1 — the
        // render-time selection threshold that makes the sample world map's dense archipelagoes
        // (Indonesia, Norway, Arctic Canada) render cleanly instead of as a black-speck noise field.
        // Removing the setting in a future refactor would make this test fail.
        //
        // Differential check, not an absolute byte-size threshold: render the same scene twice —
        // once through ConversionService (filter on at 1 px), once through MapRenderer directly
        // with otherwise-equivalent options (filter off, the renderer's default). The ConversionService
        // output must be strictly smaller, because the only difference between the two PNGs is the
        // tiny polygon's painted mark — present in the no-filter render, absent in the filtered one.
        // Two features so the data bounds are set by the big one (50° wide), making the tiny one
        // actually project to sub-pixel size — with the tiny polygon alone the data bounds would
        // shrink to ~0.001° and the polygon would fill the whole canvas in both renders.
        var subPixel = new FeatureCollection
        {
            // Big rectangle: defines the bounds, dominates the painted area.
            new Feature(new Polygon([[new(0, 0), new(50, 0), new(50, 50), new(0, 50), new(0, 0)]])),
            // Tiny rectangle at the opposite corner: ~0.001° vs ~50° of canvas = sub-pixel at any
            // sensible canvas size, well below MinFeaturePixels = 1.
            new Feature(new Polygon([[new(-25, -25), new(-24.999, -25), new(-24.999, -24.999), new(-25, -24.999), new(-25, -25)]])),
        };

        var filtered = ConversionService.RenderPng(subPixel, MapProjection.PlateCarree, 256);
        var unfiltered = MapRenderer.RenderPng(subPixel, new RenderOptions
        {
            Projection = MapProjection.PlateCarree,
            MaxDimension = 256,
            StrokeAutoScale = true,
            // MinFeaturePixels intentionally left at its default 0 — this is the "filter off"
            // baseline the wrapper's output must beat.
        });

        await Assert.That(filtered.Length).IsLessThan(unfiltered.Length)
            .Because($"filtered={filtered.Length} bytes, unfiltered={unfiltered.Length} bytes; "
                + "the only difference is the sub-pixel polygon's paint, so MinFeaturePixels=1 must drop it.");
    }

    [Test]
    public async Task Convert_RoundTripsThroughFlatGeobuf()
    {
        var fgb = ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.FlatGeobuf);
        var features = ConversionService.Read(fgb, GeoFormat.FlatGeobuf);

        await Assert.That(features.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Read_ReportsProgress()
    {
        var recorder = new Recorder();
        ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson, recorder);

        await Assert.That(recorder.Reports.All(_ => _.Phase == ProgressPhase.Reading)).IsTrue();
        await Assert.That(recorder.Reports[^1].Features).IsEqualTo(2L);
    }

    [Test]
    public async Task Write_ReportsProgress()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);
        var recorder = new Recorder();
        ConversionService.Write(features, GeoFormat.GeoJson, recorder);

        await Assert.That(recorder.Reports.All(_ => _.Phase == ProgressPhase.Writing)).IsTrue();
        await Assert.That(recorder.Reports[^1].FeatureTotal).IsEqualTo(2L);
    }

    [Test]
    public async Task RenderPng_ReportsProgress()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);
        var recorder = new Recorder();
        ConversionService.RenderPng(features, MapProjection.Auto, 256, recorder);

        await Assert.That(recorder.Reports[^1].FeatureTotal).IsEqualTo(2L);
    }

    // A synchronous progress sink. The facade invokes IProgress.Report inline as it works, so every
    // report is collected by the time the call returns. (Progress<T> instead marshals reports to a later
    // turn of the synchronization context, which races assertions on the final report.)
    sealed class Recorder : IProgress<ConvertProgress>
    {
        public List<ConvertProgress> Reports { get; } = [];

        public void Report(ConvertProgress value) =>
            Reports.Add(value);
    }

    static string ToText(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes);
}
