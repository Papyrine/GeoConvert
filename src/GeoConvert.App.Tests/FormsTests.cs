[NotInParallel]
public class FormsTests
{
    // Each window is snapshotted at 100% and 150% scale so DPI-only layout breaks (fixed-pixel sizes that
    // don't scale with the font, as the diff window's input rows once did) are caught.
    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public async Task MainWindow(int dpiPercent)
    {
        using var file = TempFile.Create("json");
        // The whole main window: menu, the "no map loaded" bar, the (empty) preview and the fixed-width
        // options column on the right.
        await Verify(WinFormsSnapshot.Render(
                () => new MainForm(SeededSettings(file), null), 1000, 680, dpiPercent / 100f))
            .UseParameters(dpiPercent);
    }

    [Test]
    [Arguments(100)]
    [Arguments(150)]
    public Task DiffWindow(int dpiPercent) =>
        // The empty compare window: the two file pickers, the mode/projection/colour toolbar, and the
        // (empty) preview / summary panes.
        Verify(WinFormsSnapshot.Render(() => new DiffForm(), 1000, 680, dpiPercent / 100f))
            .UseParameters(dpiPercent);

    // The main window with the bundled sample world map loaded — the lead documentation screenshot. Drives
    // the real load to completion (the same path File ▸ Load sample world map takes) and snapshots it; the
    // file label reads a stable "borders.fgb · …" since it shows only the file name, not the full path.
    [Test]
    public Task MainWindowWithMap()
    {
        using var file = TempFile.Create("json");
        var sample = SampleMap.Locate() ?? throw new InvalidOperationException(
            "The bundled sample world map was not staged next to the test exe (MapBundle.World).");
        return Verify(
            WinFormsSnapshot.RenderAfter(
                () => new MainForm(SeededSettings(file), null),
                _ => _.LoadAsync(sample),
                1000,
                680));
    }

    // The populated diff window, used as the documentation screenshots (referenced from nuget-readme.md).
    // A verified snapshot rather than a live screen grab: deterministic, regenerated in CI, reviewed on
    // change, and free of the PrintWindow capture artefacts (e.g. clipped edge buttons) a screen grab had.
    [Test]
    public Task DiffOverlay() => Verify(RenderDiff(DiffMode.Overlay));

    [Test]
    public Task DiffSideBySide() => Verify(RenderDiff(DiffMode.SideBySide));

    static Bitmap RenderDiff(DiffMode mode)
    {
        // Load the demo maps by bare name so the path boxes read "before.geojson" / "after.geojson"
        // (deterministic) instead of an absolute path; the fixtures sit next to the test exe.
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        try
        {
            return WinFormsSnapshot.RenderAfter(
                () => new DiffForm(mode),
                _ => _.LoadAndRenderAsync("before.geojson", "after.geojson"),
                1000,
                680);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

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

        using var file = TempFile.Create("json");
        GeoConverter.Write(SampleMaps.A(), path, GeoFormat.FlatGeobuf);
        return Verify(
            WinFormsSnapshot.RunToCompletion(
                () => new MainForm(SeededSettings(file), null),
                _ => _.LoadAsync(path),
                _ => new
                {
                    _.StatusText,
                    _.BusyIndicatorVisible,
                    _.CanSave
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

    static SettingsManager SeededSettings(string settingsPath)
    {
        // Pre-mark the first-run prompt as shown so the briefly-shown MainForm doesn't pop the (blocking)
        // association MessageBox. A throwaway temp path keeps it away from the real user settings.
        var manager = new SettingsManager(settingsPath);
        manager.Write(
            new()
            {
                AssociationsPrompted = true
            });
        return manager;
    }
}
