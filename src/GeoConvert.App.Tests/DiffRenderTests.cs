namespace GeoConvert.App.Tests;

public class DiffRenderTests
{
    // A small fixed pixel size keeps the snapshots compact and deterministic.
    static RenderSettings Settings() =>
        new() { MaxDimension = 0, Width = 400, Height = 0 };

    [Test]
    public Task Overlay() =>
        VerifyDiff(DiffMode.Overlay);

    [Test]
    public Task SideBySide() =>
        VerifyDiff(DiffMode.SideBySide);

    static Task VerifyDiff(DiffMode mode)
    {
        var png = MapDiff.Render(SampleMaps.A(), SampleMaps.B(), Settings(), mode, MapDiff.DefaultColorA, MapDiff.DefaultColorB);
        return Verify(Images.DecodePng(png)).UseParameters(mode);
    }
}
