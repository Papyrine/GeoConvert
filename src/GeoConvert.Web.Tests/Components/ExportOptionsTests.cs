// Snapshots the dynamically-loaded option control for every writable output format — one verified file
// per format, so the markup each target surfaces (or the "no options" note) is pinned. Behaviour tests
// below cover the dynamic bits: the image callback, the GeoParquet GZIP-level reveal, and the fallback.
public class ExportOptionsTests : BunitTestContext
{
    [Test]
    public Task Options_geojson() => VerifyOptions(GeoFormat.GeoJson);

    [Test]
    public Task Options_topojson() => VerifyOptions(GeoFormat.TopoJson);

    [Test]
    public Task Options_flatgeobuf() => VerifyOptions(GeoFormat.FlatGeobuf);

    [Test]
    public Task Options_kml() => VerifyOptions(GeoFormat.Kml);

    [Test]
    public Task Options_kmz() => VerifyOptions(GeoFormat.Kmz);

    [Test]
    public Task Options_gpx() => VerifyOptions(GeoFormat.Gpx);

    [Test]
    public Task Options_wkt() => VerifyOptions(GeoFormat.Wkt);

    [Test]
    public Task Options_wkb() => VerifyOptions(GeoFormat.Wkb);

    [Test]
    public Task Options_csv() => VerifyOptions(GeoFormat.Csv);

    [Test]
    public Task Options_geoparquet() => VerifyOptions(GeoFormat.GeoParquet);

    [Test]
    public Task Options_png() => VerifyOptions(GeoFormat.Png);

    [Test]
    public Task Options_svg() => VerifyOptions(GeoFormat.Svg);

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

    Task VerifyOptions(GeoFormat target)
    {
        var cut = Render<ExportOptions>(_ => _
            .Add(component => component.Target, target));

        return Verify(cut);
    }
}
