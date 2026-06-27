namespace GeoConvert.App.Tests;

[NotInParallel]
public class FormsTests
{
    [Test]
    public Task MainWindow() =>
        // The whole main window: menu, the "no map loaded" bar, the (empty) preview and the fixed-width
        // options column on the right.
        Verify(WinFormsSnapshot.Render(() => new MainForm(SeededSettings(), null), 1000, 680));

    [Test]
    public Task DiffWindow() =>
        // The empty compare window: the two file pickers, the mode/projection/colour toolbar, and the
        // (empty) preview / summary panes.
        Verify(WinFormsSnapshot.Render(() => new DiffForm(), 1000, 680));

    static SettingsManager SeededSettings()
    {
        // Pre-mark the first-run prompt as shown so the briefly-shown MainForm doesn't pop the (blocking)
        // association MessageBox. A throwaway temp path keeps it away from the real user settings.
        var manager = new SettingsManager(Path.Combine(Path.GetTempPath(), "GeoConvert.App.Tests", "settings.json"));
        manager.Write(new() { AssociationsPrompted = true });
        return manager;
    }
}
