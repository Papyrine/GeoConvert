namespace GeoConvert.App;

/// <summary>
/// The main window: open a map, see a live preview, tune the full set of render/convert options, and
/// save to any supported format — the desktop counterpart of the Blazor converter page. Reads, writes
/// and previews run off the UI thread with progress reported back through <see cref="IProgress{T}"/>.
/// </summary>
sealed class MainForm : Form
{
    readonly SettingsManager settingsManager;
    readonly RenderSettings render = new();
    readonly SimplifySettings simplify = new();
    readonly KmzSettings kmz = new();
    readonly GeoParquetSettings parquet = new();
    readonly IProgress<ConvertProgress> progress;

    OptionsPanel optionsPanel = null!;
    PictureBox preview = null!;
    Button saveButton = null!;
    Label fileLabel = null!;
    SplitContainer split = null!;
    ToolStripStatusLabel statusLabel = null!;
    ToolStripProgressBar progressBar = null!;

    FeatureCollection? features;
    string? sourcePath;
    FormatInfo? sourceFormat;
    string? initialFile;
    int previewToken;
    bool busy;

    public MainForm(SettingsManager settingsManager, string? initialFile)
    {
        this.settingsManager = settingsManager;
        this.initialFile = initialFile;
        progress = new Progress<ConvertProgress>(OnProgress);

        BuildUi();
        UpdateState();
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        FirstRun.PromptForAssociationsIfNeeded(settingsManager, this);

        // Load any file passed on the command line (the file-association open) now that the window — and
        // its handle — exists, so the read runs on the live message loop rather than from the constructor.
        if (initialFile is { } file)
        {
            initialFile = null;
            _ = LoadAsync(file);
        }
    }

    void BuildUi()
    {
        Text = "GeoConvert";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new(1100, 720);
        MinimumSize = new(820, 520);

        split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
        };

        preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(245, 245, 245),
        };
        fileLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(8, 0, 0, 0),
            Text = "No map loaded",
        };
        split.Panel1.Controls.Add(preview);
        split.Panel1.Controls.Add(fileLabel);

        optionsPanel = new(render, simplify, kmz, parquet) { Dock = DockStyle.Fill };
        optionsPanel.Changed += (_, _) => _ = RefreshPreviewAsync();
        optionsPanel.TargetChanged += (_, _) => UpdateSaveLabel();

        saveButton = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Text = "Save As…",
            Enabled = false,
        };
        saveButton.Click += (_, _) => _ = SaveAsync();
        split.Panel2.Controls.Add(optionsPanel);
        split.Panel2.Controls.Add(saveButton);

        var status = new StatusStrip();
        statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        progressBar = new ToolStripProgressBar { Visible = false, Width = 200 };
        status.Items.Add(statusLabel);
        status.Items.Add(progressBar);

        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(BuildMenu());
    }

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        SplitLayout.ConfigureSplit(split, 400);
    }

    MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Open…", null, (_, _) => OpenFile());
        file.DropDownItems.Add("&Save As…", null, (_, _) => _ = SaveAsync());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Compare maps…", null, (_, _) => new DiffForm().Show(this));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("&Associate map file types", null, (_, _) => AssociateFromMenu());
        tools.DropDownItems.Add("&Remove file associations", null, (_, _) => UnassociateFromMenu());

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => ShowAbout());

        menu.Items.Add(file);
        menu.Items.Add(tools);
        menu.Items.Add(help);
        return menu;
    }

    void OpenFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a map",
            Filter = ConversionService.BuildDialogFilter(ConversionService.ReadableFormats),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ = LoadAsync(dialog.FileName);
        }
    }

    async Task LoadAsync(string path)
    {
        var detected = ConversionService.Detect(path);
        if (detected is not { CanRead: true })
        {
            MessageBox.Show(this, $"Can't read '{Path.GetFileName(path)}': unsupported map format.", "GeoConvert", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, $"Reading {detected.DisplayName}…");
        try
        {
            var collection = await Task.Run(() => ConversionService.Read(path, detected.Format, progress));
            features = collection;
            sourcePath = path;
            sourceFormat = detected;
            fileLabel.Text = $"{Path.GetFileName(path)}  ·  {detected.DisplayName}  ·  {collection.Count} feature{(collection.Count == 1 ? "" : "s")}";
            await RefreshPreviewAsync();
        }
        catch (Exception exception)
        {
            features = null;
            ShowError("Could not read the map", exception);
        }
        finally
        {
            SetBusy(false, null);
            UpdateState();
        }
    }

    async Task RefreshPreviewAsync()
    {
        if (features is not { Count: > 0 } collection)
        {
            preview.Image?.Dispose();
            preview.Image = null;
            return;
        }

        var token = ++previewToken;
        try
        {
            var image = await Task.Run(() =>
            {
                var prepared = simplify.Apply(collection);
                var png = ConversionService.RenderPreview(prepared, render);
                return Images.DecodePng(png);
            });

            // A newer refresh started while this one ran — discard this stale image.
            if (token != previewToken)
            {
                image.Dispose();
                return;
            }

            preview.Image?.Dispose();
            preview.Image = image;
        }
        catch
        {
            // Preview is best-effort (e.g. a map with no spatial extent can't be rendered) — just leave
            // the previous image. Saving still surfaces real errors.
        }
    }

    async Task SaveAsync()
    {
        if (features is not { } collection || sourceFormat == null || busy)
        {
            return;
        }

        var format = optionsPanel.SelectedFormat;
        var info = ConversionService.Find(format)!;

        using var dialog = new SaveFileDialog
        {
            Title = "Save map",
            Filter = info.DialogFilter + "|All files (*.*)|*.*",
            FileName = Path.GetFileNameWithoutExtension(sourcePath) + info.Extension,
            DefaultExt = info.Extension.TrimStart('.'),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var destination = dialog.FileName;
        SetBusy(true, ConversionService.IsRendered(format) ? "Rendering…" : $"Writing {info.DisplayName}…");
        try
        {
            await Task.Run(() =>
            {
                var prepared = simplify.Apply(collection);
                ConversionService.Save(prepared, destination, format, render, kmz, parquet, progress);
            });
            statusLabel.Text = $"Saved {Path.GetFileName(destination)}";
        }
        catch (Exception exception)
        {
            ShowError($"Could not save as {info.DisplayName}", exception);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    void AssociateFromMenu()
    {
        try
        {
            FileAssociations.Associate();
            MessageBox.Show(this, "GeoConvert is now the handler for the supported map formats.", "GeoConvert", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError("Could not set file associations", exception);
        }
    }

    void UnassociateFromMenu()
    {
        try
        {
            FileAssociations.Unassociate();
            MessageBox.Show(this, "Removed GeoConvert's map file associations.", "GeoConvert", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError("Could not remove file associations", exception);
        }
    }

    void ShowAbout() =>
        MessageBox.Show(
            this,
            """
            GeoConvert

            Convert maps between GeoJSON, TopoJSON, Shapefile, FlatGeobuf, KML/KMZ, GPX, WKT, WKB, CSV and
            GeoParquet; render to PNG/SVG; and compare two maps.

            Tools ▸ Compare maps… diffs two files. From a terminal: geoconvert-app --help.
            """,
            "About GeoConvert",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

    void UpdateState()
    {
        saveButton.Enabled = features is { Count: > 0 } && !busy;
        UpdateSaveLabel();
    }

    void UpdateSaveLabel()
    {
        var info = ConversionService.Find(optionsPanel.SelectedFormat);
        saveButton.Text = info == null ? "Save As…" : $"Save As {info.DisplayName}…";
    }

    void SetBusy(bool value, string? message)
    {
        busy = value;
        progressBar.Visible = value;
        if (!value)
        {
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 0;
        }

        if (message != null)
        {
            statusLabel.Text = message;
        }

        saveButton.Enabled = features is { Count: > 0 } && !busy;
        Cursor = value ? Cursors.AppStarting : Cursors.Default;
    }

    void OnProgress(ConvertProgress report)
    {
        if (!busy)
        {
            return;
        }

        if (report.Fraction is { } fraction)
        {
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = (int) Math.Clamp(fraction * 100, 0, 100);
        }
        else
        {
            // No derivable fraction (e.g. the read phase, where the total isn't known yet) — show motion
            // rather than a frozen bar.
            progressBar.Style = ProgressBarStyle.Marquee;
        }
    }

    void ShowError(string action, Exception exception) =>
        MessageBox.Show(this, $"{action}:\n{exception.Message}", "GeoConvert", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
