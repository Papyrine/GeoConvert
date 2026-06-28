namespace GeoConvert.App;

/// <summary>
/// The export options editor — the desktop equivalent of the Blazor app's ExportOptions component.
/// Surfaces every <see cref="RenderSettings"/> knob plus the per-format options (KMZ deflate level,
/// GeoParquet codec) and the optional <see cref="SimplifySettings"/> pre-pass, showing only the
/// sections relevant to the chosen output format. Raises <see cref="Changed"/> when a preview-affecting
/// knob moves and <see cref="TargetChanged"/> when the output format changes.
/// </summary>
sealed class OptionsPanel : FlowLayoutPanel
{
    readonly RenderSettings render;
    readonly SimplifySettings simplify;
    readonly KmzSettings kmz;
    readonly GeoParquetSettings parquet;

    GroupBox imageSection = null!;
    GroupBox pngSection = null!;
    GroupBox svgSection = null!;
    GroupBox kmzSection = null!;
    GroupBox parquetSection = null!;
    GroupBox noteSection = null!;
    ComboBox outputCombo = null!;

    TableLayoutPanel currentTable = null!;

    public OptionsPanel(RenderSettings render, SimplifySettings simplify, KmzSettings kmz, GeoParquetSettings parquet)
    {
        this.render = render;
        this.simplify = simplify;
        this.kmz = kmz;
        this.parquet = parquet;

        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        Padding = new(4);

        BuildProjectionSection();
        BuildOutputSection();
        BuildImageSection();
        BuildPngSection();
        BuildSvgSection();
        BuildKmzSection();
        BuildParquetSection();
        BuildSimplifySection();
        BuildNoteSection();

        UpdateVisibility();
    }

    /// <summary>The currently selected output format.</summary>
    public GeoFormat SelectedFormat { get; private set; } = GeoFormat.Kml;

    /// <summary>Raised when a preview-affecting option changes (so the host can re-render the preview).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the output format changes.</summary>
    public event EventHandler? TargetChanged;

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // --- sections ---

    void BuildProjectionSection()
    {
        // Above Output and always shown: the projection drives the live preview too, not just the
        // PNG/SVG export, so it stays available whatever the chosen output format.
        BeginSection("Projection");
        AddRadioGroup(OptionChoices.Projections, render.Projection, _ => render.Projection = _);
    }

    void BuildOutputSection()
    {
        BeginSection("Output");
        outputCombo = AddCombo(
            "Format",
            [.. ConversionService.WritableFormats.Select(_ => (_.Format, _.DisplayName))],
            SelectedFormat,
            value =>
            {
                SelectedFormat = value;
                UpdateVisibility();
                TargetChanged?.Invoke(this, EventArgs.Empty);
            });
    }

    /// <summary>Selects the output format programmatically, exactly as choosing it in the combo would.</summary>
    internal void SelectFormat(GeoFormat format) => Combos.Select(outputCombo, format);

    void BuildImageSection()
    {
        imageSection = BeginSection("Image (PNG / SVG)");
        AddCombo("Resolution", OptionChoices.Dimensions, render.MaxDimension, _ => render.MaxDimension = _);
        AddInt("Padding (px)", 0, 500, render.Padding, _ => render.Padding = _);
        AddInt("Stroke width (px)", 0, 50, render.StrokeWidth, _ => render.StrokeWidth = _);
        AddInt("Point radius (px)", 0, 50, render.PointRadius, _ => render.PointRadius = _);
        AddCheck("Auto-scale strokes to zoom", render.StrokeAutoScale, _ => render.StrokeAutoScale = _);
        AddDouble("Min feature size (px)", 0, 64, 1, render.MinFeaturePixels, _ => render.MinFeaturePixels = _);
        AddCheck("Show labels", render.Labels, _ => render.Labels = _);
        AddText("Label property (blank = auto)", render.LabelProperty ?? string.Empty, _ => render.LabelProperty = _);
        AddDouble("Label size (px)", 1, 200, 1, render.LabelSize, _ => render.LabelSize = _);
        AddColor("Background", () => render.Background, _ => render.Background = _, withAlpha: false);
        AddCheck("Ocean fill", render.OceanEnabled, _ => render.OceanEnabled = _);
        AddColor("Ocean colour", () => render.Ocean, _ => render.Ocean = _, withAlpha: true);
        AddColor("Stroke", () => render.Stroke, _ => render.Stroke = _, withAlpha: false);
        AddColor("Polygon fill", () => render.Fill, _ => render.Fill = _, withAlpha: true);
        AddColor("Label text", () => render.LabelColor, _ => render.LabelColor = _, withAlpha: false);
        AddCheck("Label halo", render.HaloEnabled, _ => render.HaloEnabled = _);
        AddColor("Halo colour", () => render.LabelHalo, _ => render.LabelHalo = _, withAlpha: true);
        AddCheck("Label knockout", render.KnockoutEnabled, _ => render.KnockoutEnabled = _);
        AddColor("Knockout colour", () => render.LabelKnockout, _ => render.LabelKnockout = _, withAlpha: true);
    }

    void BuildPngSection()
    {
        pngSection = BeginSection("PNG");
        AddCombo("Renderer", OptionChoices.Renderers, render.Renderer, _ => render.Renderer = _);
        AddCombo("Compression", OptionChoices.CompressionLevels, render.PngCompression, _ => render.PngCompression = _, affectsPreview: false);
    }

    void BuildSvgSection()
    {
        svgSection = BeginSection("SVG");
        AddDouble("Simplify tolerance (px)", 0, 20, 1, render.SvgSimplifyTolerance, _ => render.SvgSimplifyTolerance = _, affectsPreview: false);
    }

    void BuildKmzSection()
    {
        kmzSection = BeginSection("KMZ");
        AddCombo("Compression", OptionChoices.CompressionLevels, kmz.Compression, _ => kmz.Compression = _, affectsPreview: false);
    }

    void BuildParquetSection()
    {
        parquetSection = BeginSection("GeoParquet");
        AddCombo("Codec", OptionChoices.ParquetCodecs, parquet.Codec, _ => parquet.Codec = _, affectsPreview: false);
        AddCombo("GZIP level", OptionChoices.CompressionLevels, parquet.GzipLevel, _ => parquet.GzipLevel = _, affectsPreview: false);
    }

    void BuildSimplifySection()
    {
        BeginSection("Simplify (optional pre-pass)");
        var enabled = AddCheck("Simplify geometry", simplify.Enabled, _ => simplify.Enabled = _);

        // The tolerance / method / topology options only apply when simplification is on, so collapse
        // them when "Simplify geometry" is unchecked. Capture the controls added below so the toggle can
        // hide both the labels and the inputs (an AutoSize TableLayoutPanel row collapses when empty).
        var dependentStart = currentTable.Controls.Count;
        AddDouble("Tolerance", 0, 1000, 4, simplify.Tolerance, _ => simplify.Tolerance = _);
        AddCombo(
            "Method",
            [(SimplifyMethod.DouglasPeucker, "Douglas–Peucker"), (SimplifyMethod.Visvalingam, "Visvalingam")],
            simplify.Method,
            _ => simplify.Method = _);
        AddCheck("Preserve shared boundaries", simplify.Topology, _ => simplify.Topology = _);

        var dependents = new List<Control>();
        for (var index = dependentStart; index < currentTable.Controls.Count; index++)
        {
            dependents.Add(currentTable.Controls[index]);
        }

        void Sync()
        {
            foreach (var dependent in dependents)
            {
                dependent.Visible = enabled.Checked;
            }
        }

        enabled.CheckedChanged += (_, _) => Sync();
        Sync();
    }

    void BuildNoteSection()
    {
        noteSection = BeginSection("Format");
        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new(400, 0),
            Margin = new(3),
            Text = "This format writes geometry and properties directly. Use the Simplify section above to thin vertices before writing.",
        };
        currentTable.Controls.Add(note);
        currentTable.SetColumnSpan(note, 2);
    }

    void UpdateVisibility()
    {
        var format = SelectedFormat;
        imageSection.Visible = ConversionService.IsRendered(format);
        pngSection.Visible = format == GeoFormat.Png;
        svgSection.Visible = format == GeoFormat.Svg;
        kmzSection.Visible = format == GeoFormat.Kmz;
        parquetSection.Visible = format == GeoFormat.GeoParquet;
        noteSection.Visible = format is not (GeoFormat.Png or GeoFormat.Svg or GeoFormat.Kmz or GeoFormat.GeoParquet);
    }

    // --- row/control builders ---

    GroupBox BeginSection(string title)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new(4),
        };
        table.ColumnStyles.Add(new(SizeType.Absolute, 215));
        table.ColumnStyles.Add(new(SizeType.Absolute, 200));

        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // Pin the width (Min == Max), leaving only the height to AutoSize. Without this an AutoSize
            // GroupBox wrapping a Dock=Top table can't resolve its width (each defers to the other) and
            // collapses to a sliver.
            MinimumSize = new(440, 0),
            MaximumSize = new(440, 0),
            Margin = new(3),
            Padding = new(6, 3, 6, 6),
        };
        box.Controls.Add(table);
        Controls.Add(box);
        currentTable = table;
        return box;
    }

    void Row(string label, Control control)
    {
        // Fill the label cell and centre its text vertically so it lines up with the input regardless of
        // the input's height; centre the input in the row too (Anchor without Top/Bottom). The column is
        // wide enough (215px) that no label wraps.
        var caption = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new(3, 0, 3, 0),
        };
        currentTable.Controls.Add(caption);
        control.Anchor = AnchorStyles.Left;
        currentTable.Controls.Add(control);
    }

    // A full-width column of mutually-exclusive radio buttons (one container => one radio group). Used
    // where a choice reads better laid out than hidden in a dropdown — e.g. the projection.
    void AddRadioGroup<T>(IReadOnlyList<(T Value, string Label)> choices, T current, Action<T> set, bool affectsPreview = true)
        where T : notnull
    {
        var group = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new(3),
        };
        foreach (var (value, text) in choices)
        {
            var radio = new RadioButton
            {
                Text = text,
                AutoSize = true,
                Checked = EqualityComparer<T>.Default.Equals(value, current),
                Margin = new(0, 1, 0, 1),
            };
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked)
                {
                    return;
                }

                set(value);
                if (affectsPreview)
                {
                    RaiseChanged();
                }
            };
            group.Controls.Add(radio);
        }

        currentTable.Controls.Add(group);
        currentTable.SetColumnSpan(group, 2);
    }

    ComboBox AddCombo<T>(string label, IReadOnlyList<(T Value, string Label)> choices, T current, Action<T> set, bool affectsPreview = true)
        where T : notnull
    {
        var combo = Combos.Build(
            choices,
            current,
            value =>
            {
                set(value);
                if (affectsPreview)
                {
                    RaiseChanged();
                }
            });
        combo.Width = 190;
        Row(label, combo);
        return combo;
    }

    void AddInt(string label, int min, int max, int current, Action<int> set, bool affectsPreview = true)
    {
        var numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(current, min, max),
            Width = 190,
            Margin = new(3),
        };
        numeric.ValueChanged += (_, _) =>
        {
            set((int) numeric.Value);
            if (affectsPreview)
            {
                RaiseChanged();
            }
        };
        Row(label, numeric);
    }

    void AddDouble(string label, double min, double max, int decimals, double current, Action<double> set, bool affectsPreview = true)
    {
        var numeric = new NumericUpDown
        {
            Minimum = (decimal) min,
            Maximum = (decimal) max,
            DecimalPlaces = decimals,
            Increment = (decimal) Math.Pow(10, -decimals),
            Value = (decimal) Math.Clamp(current, min, max),
            Width = 190,
            Margin = new(3),
        };
        numeric.ValueChanged += (_, _) =>
        {
            set((double) numeric.Value);
            if (affectsPreview)
            {
                RaiseChanged();
            }
        };
        Row(label, numeric);
    }

    CheckBox AddCheck(string label, bool current, Action<bool> set, bool affectsPreview = true)
    {
        var check = new CheckBox
        {
            Checked = current,
            AutoSize = true,
            Margin = new(3),
        };
        check.CheckedChanged += (_, _) =>
        {
            set(check.Checked);
            if (affectsPreview)
            {
                RaiseChanged();
            }
        };
        Row(label, check);
        return check;
    }

    void AddText(string label, string current, Action<string?> set, bool affectsPreview = true)
    {
        var text = new TextBox
        {
            Text = current,
            Width = 190,
            Margin = new(3),
        };
        text.TextChanged += (_, _) =>
        {
            set(text.Text.Length == 0 ? null : text.Text);
            if (affectsPreview)
            {
                RaiseChanged();
            }
        };
        Row(label, text);
    }

    void AddColor(string label, Func<Rgba> get, Action<Rgba> set, bool withAlpha, bool affectsPreview = true)
    {
        var holder = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new(3),
        };

        var swatch = new Button
        {
            Width = 44,
            Height = 24,
            BackColor = Opaque(get()),
            FlatStyle = FlatStyle.Flat,
            Margin = new(0, 0, 6, 0),
        };
        swatch.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = Opaque(get()), FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var updated = get().WithRgbOf(dialog.Color);
                set(updated);
                swatch.BackColor = Opaque(updated);
                if (affectsPreview)
                {
                    RaiseChanged();
                }
            }
        };
        holder.Controls.Add(swatch);

        if (withAlpha)
        {
            var alpha = new TrackBar
            {
                Minimum = 0,
                Maximum = 255,
                Value = get().A,
                Width = 120,
                Height = 26,
                TickStyle = TickStyle.None,
            };
            alpha.ValueChanged += (_, _) =>
            {
                set(get() with { A = (byte) alpha.Value });
                if (affectsPreview)
                {
                    RaiseChanged();
                }
            };
            holder.Controls.Add(alpha);
        }

        Row(label, holder);
    }

    // A WinForms button can't render alpha, so the swatch shows the opaque RGB; the alpha slider conveys
    // transparency for the colours that carry it.
    static Color Opaque(Rgba color) => Color.FromArgb(255, color.R, color.G, color.B);
}
