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
        summary = new TextBox
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

        saveButton = new Button
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
            Height = 84,
            Padding = new(6, 6, 6, 2),
        };
        table.ColumnStyles.Add(new(SizeType.Absolute, 60));
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        table.ColumnStyles.Add(new(SizeType.Absolute, 90));
        table.RowStyles.Add(new(SizeType.Absolute, 38));
        table.RowStyles.Add(new(SizeType.Absolute, 38));

        pathBoxA = AddInputRow(table, "Map A:", pathA, _ => LoadAInto(_));
        pathBoxB = AddInputRow(table, "Map B:", pathB, _ => LoadBInto(_));
        return table;
    }

    TextBox AddInputRow(TableLayoutPanel table, string label, string? value, Action<string> onPicked)
    {
        // Anchor (not Dock) so each control keeps its natural height and the TableLayoutPanel centres it
        // vertically in the row — Dock=Fill here stretched the controls and overlapped the toolbar below.
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 0, 3, 0) });
        var box = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, ReadOnly = true, Text = value ?? string.Empty, Margin = new(3, 0, 3, 0) };
        table.Controls.Add(box);
        var browse = new Button { Text = "Browse…", Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new(3, 0, 3, 0) };
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

    FlowLayoutPanel BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new(6, 2, 6, 2),
        };

        bar.Controls.Add(new Label { Text = "Mode", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 8, 3, 3) });
        bar.Controls.Add(Combos.Build(
            [(DiffMode.Overlay, "Overlay"), (DiffMode.SideBySide, "Side by side")],
            mode,
            value =>
            {
                mode = value;
                _ = RenderAsync();
            }));

        bar.Controls.Add(new Label { Text = "Projection", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(8, 8, 3, 3) });
        bar.Controls.Add(Combos.Build(
            OptionChoices.Projections,
            settings.Projection,
            value =>
            {
                settings.Projection = value;
                _ = RenderAsync();
            }));

        bar.Controls.Add(new Label { Text = "Resolution", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(8, 8, 3, 3) });
        bar.Controls.Add(Combos.Build(
            OptionChoices.Dimensions,
            settings.MaxDimension > 0 ? settings.MaxDimension : 2048,
            value =>
            {
                settings.MaxDimension = value;
                _ = RenderAsync();
            }));

        bar.Controls.Add(new Label { Text = "A", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(8, 8, 1, 3) });
        swatchA = ColorSwatch(() => colorA, _ => colorA = _);
        bar.Controls.Add(swatchA);
        bar.Controls.Add(new Label { Text = "B", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(8, 8, 1, 3) });
        swatchB = ColorSwatch(() => colorB, _ => colorB = _);
        bar.Controls.Add(swatchB);

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

    async Task LoadAsync(string path, bool isFirst, bool render = true)
    {
        try
        {
            var collection = await Task.Run(() => GeoConverter.Read(path));
            if (isFirst)
            {
                mapA = collection;
                pathA = path;
            }
            else
            {
                mapB = collection;
                pathB = path;
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
