namespace GeoConvert.App.Tests;

// WinForms rendering wants a single UI thread, so keep these off the parallel runner.
[NotInParallel]
public class OptionsPanelTests
{
    [Test]
    public Task Kml() =>
        // A plain vector format: the always-on Projection radios plus the Output, Simplify and
        // format-note sections (no image options).
        Verify(WinFormsSnapshot.Render(() => Panel(GeoFormat.Kml), 480, 560));

    [Test]
    public Task Png() =>
        // The image formats reveal the full render-options section (projection, strokes, labels, colours)
        // plus the PNG sub-section, so this covers most of the options UI and its show/hide logic.
        Verify(WinFormsSnapshot.Render(() => Panel(GeoFormat.Png), 480, 1320));

    static OptionsPanel Panel(GeoFormat format)
    {
        var panel = new OptionsPanel(new(), new(), new(), new());
        panel.SelectFormat(format);
        return panel;
    }
}
