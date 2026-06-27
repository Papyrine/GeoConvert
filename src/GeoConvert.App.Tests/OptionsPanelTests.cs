namespace GeoConvert.App.Tests;

// WinForms rendering wants a single UI thread, so keep these off the parallel runner.
[NotInParallel]
public class OptionsPanelTests
{
    [Test]
    public Task Kml() =>
        // A plain vector format: only the Output, Simplify and format-note sections show.
        Verify(WinFormsSnapshot.Render(() => Panel(GeoFormat.Kml), 420, 380));

    [Test]
    public Task Png() =>
        // The image formats reveal the full render-options section (projection, strokes, labels, colours)
        // plus the PNG sub-section, so this covers most of the options UI and its show/hide logic.
        Verify(WinFormsSnapshot.Render(() => Panel(GeoFormat.Png), 420, 1320));

    static OptionsPanel Panel(GeoFormat format)
    {
        var panel = new OptionsPanel(new(), new(), new(), new());
        panel.SelectFormat(format);
        return panel;
    }
}
