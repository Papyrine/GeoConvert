namespace GeoConvert.App.Tests;

// WinForms rendering wants a single UI thread, so keep these off the parallel runner.
[NotInParallel]
public class OptionsPanelTests
{
    // Snapshotted at 100% and 150% scale so DPI-only layout breaks (fixed-pixel sizes that don't scale
    // with the font) are caught, not just the 96-DPI layout.
    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public Task Kml(int dpiPercent) =>
        // A plain vector format: the always-on Projection radios plus the Output, Simplify and
        // format-note sections (no image options).
        Verify(WinFormsSnapshot.Render(() => Panel(GeoFormat.Kml), 480, 560, dpiPercent / 100f))
            .UseParameters(dpiPercent);

    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public Task Png(int dpiPercent) =>
        // The image formats reveal the full render-options section (projection, strokes, labels, colours)
        // plus the PNG sub-section, so this covers most of the options UI and its show/hide logic.
        Verify(WinFormsSnapshot.Render(() => Panel(GeoFormat.Png), 480, 1320, dpiPercent / 100f))
            .UseParameters(dpiPercent);

    static OptionsPanel Panel(GeoFormat format)
    {
        var panel = new OptionsPanel(new(), new(), new(), new());
        panel.SelectFormat(format);
        return panel;
    }
}
