namespace GeoConvert.App.Tests;

public class DiffSummaryTests
{
    [Test]
    public Task Summarize() =>
        Verify(MapDiff.Summarize("a.geojson", SampleMaps.A(), "b.geojson", SampleMaps.B()));
}
