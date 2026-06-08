// Snapshots the dynamically-loaded option control for every writable output format — one verified file
// per format, so the markup each target surfaces (or the "no options" note) is pinned. Behaviour tests
// below cover the dynamic bits: the image callback, the GeoParquet GZIP-level reveal, and the fallback.
public class ExportOptionsTests : BunitTestContext
{
    [Test]
    public Task Options_geojson()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.GeoJson));

        return Verify(cut);
    }

    [Test]
    public Task Options_topojson()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.TopoJson));

        return Verify(cut);
    }

    [Test]
    public Task Options_flatgeobuf()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.FlatGeobuf));

        return Verify(cut);
    }

    [Test]
    public Task Options_kml()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Kml));

        return Verify(cut);
    }

    [Test]
    public Task Options_kmz()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Kmz));

        return Verify(cut);
    }

    [Test]
    public Task Options_gpx()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Gpx));

        return Verify(cut);
    }

    [Test]
    public Task Options_wkt()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Wkt));

        return Verify(cut);
    }

    [Test]
    public Task Options_wkb()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Wkb));

        return Verify(cut);
    }

    [Test]
    public Task Options_csv()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Csv));

        return Verify(cut);
    }

    [Test]
    public Task Options_geoparquet()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.GeoParquet));

        return Verify(cut);
    }

    [Test]
    public Task Options_png()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Png));

        return Verify(cut);
    }

    [Test]
    public Task Options_svg()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Svg));

        return Verify(cut);
    }

    [Test]
    public async Task PlainFormat_ShowsNoOptionsNote()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.GeoJson));

        await Assert.That(cut.FindAll(".no-options").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".image-options").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Image_ChangeRaisesOnRenderChanged()
    {
        var raised = false;
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.Png)
            .Add(component => component.OnRenderChanged, () => raised = true));

        await EventHandlerDispatchExtensions.ChangeAsync(cut.Find("#projection-select"), nameof(MapProjection.PlateCarree));

        await Assert.That(raised).IsTrue();
    }

    [Test]
    public async Task GeoParquet_GzipLevelShownOnlyForGzipCodec()
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, GeoFormat.GeoParquet)
            .Add(component => component.Parquet, new GeoParquetSettings()));

        // Snappy (the default) ignores the deflate level, so the GZIP-level control is hidden.
        await Assert.That(cut.FindAll("#parquet-gzip").Count).IsEqualTo(0);

        await EventHandlerDispatchExtensions.ChangeAsync(cut.Find("#parquet-codec"), nameof(ParquetCompression.Gzip));

        await Assert.That(cut.FindAll("#parquet-gzip").Count).IsEqualTo(1);
    }
}
