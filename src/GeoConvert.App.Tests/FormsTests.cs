namespace GeoConvert.App.Tests;

[NotInParallel]
public class FormsTests
{
    // Each window is snapshotted at 100% and 150% scale so DPI-only layout breaks (fixed-pixel sizes that
    // don't scale with the font, as the diff window's input rows once did) are caught.
    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public Task MainWindow(int dpiPercent) =>
        // The whole main window: menu, the "no map loaded" bar, the (empty) preview and the fixed-width
        // options column on the right.
        Verify(WinFormsSnapshot.Render(() => new MainForm(SeededSettings(), null), 1000, 680, dpiPercent / 100f))
            .UseParameters(dpiPercent);

    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public Task DiffWindow(int dpiPercent) =>
        // The empty compare window: the two file pickers, the mode/projection/colour toolbar, and the
        // (empty) preview / summary panes.
        Verify(WinFormsSnapshot.Render(() => new DiffForm(), 1000, 680, dpiPercent / 100f))
            .UseParameters(dpiPercent);

    [Test]
    public Task StatusBarAfterLoad()
    {
        // A finished read must settle the status bar to a "Loaded …" idle. Regression guard: the load's
        // finally calls SetBusy(false, null), which leaves the label untouched, so without an explicit
        // completion message the transient "Reading FlatGeobuf…" stayed stuck on screen after the map had
        // loaded. Drive a real load to completion and snapshot the settled state.
        using var directory = new TempDirectory();
        // FlatGeobuf (the format from the original report) under a fixed file name, so the status reads a
        // stable "Loaded sample.fgb". RunToCompletion blocks until the load has read the file, so the temp
        // directory can be disposed as the method returns.
        var path = Path.Combine(directory, "sample.fgb");
        GeoConverter.Write(SampleMaps.A(), path, GeoFormat.FlatGeobuf);
        return Verify(
            WinFormsSnapshot.RunToCompletion(
                () => new MainForm(SeededSettings(), null),
                form => form.LoadAsync(path),
                form => new
                {
                    form.StatusText,
                    form.BusyIndicatorVisible,
                    form.CanSave
                },
                1000,
                680));
    }

    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public Task About(int dpiPercent) =>
        // The (auto-sizing) About dialog: title, description, the clickable project link and OK button.
        Verify(WinFormsSnapshot.Render(() => new AboutForm(), 420, 220, dpiPercent / 100f))
            .UseParameters(dpiPercent);

    static SettingsManager SeededSettings()
    {
        // Pre-mark the first-run prompt as shown so the briefly-shown MainForm doesn't pop the (blocking)
        // association MessageBox. A throwaway temp path keeps it away from the real user settings.
        var manager = new SettingsManager(Path.Combine(Path.GetTempPath(), "GeoConvert.App.Tests", "settings.json"));
        manager.Write(new() { AssociationsPrompted = true });
        return manager;
    }
}
