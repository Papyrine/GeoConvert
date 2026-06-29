namespace GeoConvert.App;

/// <summary>
/// The map comparison window. Pick two maps (or arrive preloaded from the <c>diff</c> command line),
/// see the visual diff — an overlay of both in distinct colours, or a side-by-side at a shared extent —
/// alongside a structural summary (feature counts, geometry histograms, bounds, property deltas), and
/// save the diff image.
/// </summary>
sealed class DiffForm : Form
{
    readonly RenderSettings settings;
    DiffMode mode;
    Rgba colorA;
    Rgba colorB;
    string? pathA;
    string? pathB;

    FeatureCollection? mapA;
    FeatureCollection? mapB;
    byte[]? currentImage;
    int renderToken;
    bool initialLoadDone;

    TextBox pathBoxA = null!;
    TextBox pathBoxB = null!;
    PictureBox preview = null!;
    TextBox summary = null!;
    Button saveButton = null!;
    Button swatchA = null!;
    Button swatchB = null!;
    SplitContainer split = null!;

    public DiffForm()
        : this(new(), DiffMode.Overlay, MapDiff.DefaultColorA, MapDiff.DefaultColorB, null, null)
    {
    }

    public DiffForm(Cli.DiffRequest request)
        : this(request.Settings, request.Mode, request.ColorA, request.ColorB, request.PathA, request.PathB)
    {
    }

    // Test seam: build the window with the mode preset (so the toolbar's Mode combo matches) but no paths,
    // so OnShown doesn't auto-load. A snapshot then populates it deterministically via LoadAndRenderAsync.
    internal DiffForm(DiffMode mode)
        : this(new(), mode, MapDiff.DefaultColorA, MapDiff.DefaultColorB, null, null)
    {
    }

    DiffForm(RenderSettings settings, DiffMode mode, Rgba colorA, Rgba colorB, string? pathA, string? pathB)
    {
        this.settings = settings;
        this.mode = mode;
        this.colorA = colorA;
        this.colorB = colorB;
        this.pathA = pathA;
        this.pathB = pathB;
        BuildUi();
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        if (initialLoadDone)
        {
            return;
        }

        initialLoadDone = true;
        if (pathA != null && pathB != null)
        {
            BeginInvoke(() => _ = LoadBothAsync());
        }
    }

    void BuildUi()
    {
        Text = "Compare maps — GeoConvert";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new(1100, 720);
        MinimumSize = new(820, 520);

        split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
        };
        preview = new()
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(245, 245, 245),
        };
        summary = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new("Consolas", 9F),
            BackColor = Color.White,
        };
        split.Panel1.Controls.Add(preview);
        split.Panel2.Controls.Add(summary);

        saveButton = new()
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            Text = "Save diff image…",
            Enabled = false,
        };
        saveButton.Click += (_, _) => SaveImage();

        Controls.Add(split);
        Controls.Add(saveButton);
        Controls.Add(BuildToolbar());
        Controls.Add(BuildInputs());
    }

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        SplitLayout.ConfigureSplit(split, 360);
    }

    TableLayoutPanel BuildInputs()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 2,
            // Auto-size the rows (rather than a fixed-height Absolute row) so they scale with the display
            // DPI; a fixed row height left the Dock=Fill labels top-aligned against the (DPI-scaled) text
            // boxes at 125%+.
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new(6, 6, 6, 4),
        };
        table.ColumnStyles.Add(new(SizeType.Absolute, 60));
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        table.ColumnStyles.Add(new(SizeType.Absolute, 90));

        pathBoxA = AddInputRow(table, "Map A:", pathA, _ => LoadAInto(_));
        pathBoxB = AddInputRow(table, "Map B:", pathB, _ => LoadBInto(_));
        return table;
    }

    TextBox AddInputRow(TableLayoutPanel table, string label, string? value, Action<string> onPicked)
    {
        // The label fills its fixed-height cell and centres its text vertically (MiddleLeft), so it lines
        // up with the text box. The box and button anchor left+right at their natural height and the
        // TableLayoutPanel centres them in the row.
        table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new(3, 5, 3, 5) });
        var box = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, ReadOnly = true, Text = value ?? string.Empty, Margin = new(3, 5, 3, 5) };
        table.Controls.Add(box);
        var browse = new Button { Text = "Browse…", Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new(3, 5, 3, 5) };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = label,
                Filter = ConversionService.BuildDialogFilter(ConversionService.ReadableFormats),
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                box.Text = dialog.FileName;
                onPicked(dialog.FileName);
            }
        };
        table.Controls.Add(browse);
        return box;
    }

    TableLayoutPanel BuildToolbar()
    {
        // A single-row TableLayoutPanel (not a FlowLayoutPanel) so each item anchors left and the cell
        // centres it vertically — labels line up with the combos at any DPI. A FlowLayoutPanel top-aligns
        // its children, which needed hand-tuned top margins that drifted once the controls scaled.
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 10,
            RowCount = 1,
            Padding = new(6, 2, 6, 2),
        };
        for (var column = 0; column < bar.ColumnCount; column++)
        {
            bar.ColumnStyles.Add(new(SizeType.AutoSize));
        }

        void Add(Control control, int gapLeft)
        {
            control.Anchor = AnchorStyles.Left;
            control.Margin = new(gapLeft, 3, 3, 3);
            bar.Controls.Add(control);
        }

        Add(new Label { Text = "Mode", AutoSize = true }, 3);
        Add(
            Combos.Build(
                [(DiffMode.Overlay, "Overlay"), (DiffMode.SideBySide, "Side by side")],
                mode,
                value =>
                {
                    mode = value;
                    _ = RenderAsync();
                }),
            0);

        Add(new Label { Text = "Projection", AutoSize = true }, 10);
        Add(
            Combos.Build(
                OptionChoices.Projections,
                settings.Projection,
                value =>
                {
                    settings.Projection = value;
                    _ = RenderAsync();
                }),
            0);

        Add(new Label { Text = "Resolution", AutoSize = true }, 10);
        Add(
            Combos.Build(
                OptionChoices.Dimensions,
                settings.MaxDimension > 0 ? settings.MaxDimension : 2048,
                value =>
                {
                    settings.MaxDimension = value;
                    _ = RenderAsync();
                }),
            0);

        Add(new Label { Text = "A", AutoSize = true }, 10);
        swatchA = ColorSwatch(() => colorA, _ => colorA = _);
        Add(swatchA, 1);
        Add(new Label { Text = "B", AutoSize = true }, 8);
        swatchB = ColorSwatch(() => colorB, _ => colorB = _);
        Add(swatchB, 1);

        return bar;
    }

    Button ColorSwatch(Func<Rgba> get, Action<Rgba> set)
    {
        var current = get();
        var swatch = new Button
        {
            Width = 40,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, current.R, current.G, current.B),
            Margin = new(1, 4, 1, 3),
        };
        swatch.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                set(dialog.Color.ToRgba());
                swatch.BackColor = Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                _ = RenderAsync();
            }
        };
        return swatch;
    }

    void LoadAInto(string path) => _ = LoadAsync(path, isFirst: true);

    void LoadBInto(string path) => _ = LoadAsync(path, isFirst: false);

    async Task LoadBothAsync()
    {
        await LoadAsync(pathA!, isFirst: true, render: false);
        await LoadAsync(pathB!, isFirst: false, render: false);
        await RenderAsync();
    }

    // Test seam: load both maps and render the diff, awaitable to completion so a snapshot captures the
    // fully populated window. Mirrors the OnShown auto-load but driven explicitly.
    internal Task LoadAndRenderAsync(string firstPath, string secondPath)
    {
        pathA = firstPath;
        pathB = secondPath;
        return LoadBothAsync();
    }

    async Task LoadAsync(string path, bool isFirst, bool render = true)
    {
        try
        {
            var collection = await Task.Run(() => GeoConverter.Read(path));
            if (isFirst)
            {
                mapA = collection;
                pathA = path;
                pathBoxA.Text = path;
            }
            else
            {
                mapB = collection;
                pathB = path;
                pathBoxB.Text = path;
            }

            if (render)
            {
                await RenderAsync();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not read '{Path.GetFileName(path)}':\n{exception.Message}", "GeoConvert", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task RenderAsync()
    {
        if (mapA is not { } a || mapB is not { } b)
        {
            return;
        }

        var token = ++renderToken;
        var localMode = mode;
        var localColorA = colorA;
        var localColorB = colorB;
        try
        {
            var result = await Task.Run(() =>
            {
                var image = MapDiff.Render(a, b, settings, localMode, localColorA, localColorB);
                var text = MapDiff.Summarize(Path.GetFileName(pathA!), a, Path.GetFileName(pathB!), b);
                return (image, text);
            });

            if (token != renderToken)
            {
                return;
            }

            currentImage = result.image;
            preview.Image?.Dispose();
            preview.Image = Images.DecodePng(result.image);
            summary.Text = result.text.ReplaceLineEndings();
            saveButton.Enabled = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not render the diff:\n{exception.Message}", "GeoConvert", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SaveImage()
    {
        if (currentImage is not { } image)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save diff image",
            Filter = "PNG image (*.png)|*.png",
            FileName = "diff.png",
            DefaultExt = "png",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllBytes(dialog.FileName, image);
        }
    }
}
