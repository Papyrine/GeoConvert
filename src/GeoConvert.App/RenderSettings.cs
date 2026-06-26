namespace GeoConvert.App;

/// <summary>
/// User-tunable knobs for the PNG/SVG image export — a desktop superset of the Blazor app's render
/// settings. It carries every <see cref="RenderOptions"/> knob the GUI and CLI surface (the web app's
/// projection / size / colours / strokes / labels) plus the extras a desktop with a filesystem can
/// afford: an explicit render extent (<see cref="Bounds"/>), an explicit pixel size, the PNG
/// <see cref="Renderer"/> backend, and the label knockout. Defaults mirror <see cref="RenderOptions"/>'s
/// own, with the ocean fill and sub-pixel feature culling pre-enabled, so an untouched instance renders
/// the same map the Blazor preview did.
/// </summary>
public sealed class RenderSettings
{
    // Size & layout.
    public MapProjection Projection { get; set; } = MapProjection.Auto;
    public RendererBackend Renderer { get; set; } = RendererBackend.BuiltIn;

    /// <summary>When &gt; 0, caps the longer edge at this many pixels (fit-to-box) and ignores
    /// <see cref="Width"/>/<see cref="Height"/>. The default matches the Blazor preview.</summary>
    public int MaxDimension { get; set; } = 2048;
    public int Width { get; set; } = 2048;
    public int Height { get; set; }

    /// <summary>Render extent in lon/lat. Null renders the data bounds (the common case).</summary>
    public Envelope? Bounds { get; set; }
    public int Padding { get; set; } = 8;

    // Strokes & features.
    public int StrokeWidth { get; set; } = 2;
    public int PointRadius { get; set; } = 4;
    public bool StrokeAutoScale { get; set; } = true;
    public double MinFeaturePixels { get; set; } = 1;

    // Labels.
    public bool Labels { get; set; }

    /// <summary>The property whose value labels each feature. Null/blank falls back to the common
    /// name-like keys (name, NAME, admin, …) then the feature id — the Blazor app's behaviour.</summary>
    public string? LabelProperty { get; set; }
    public double LabelSize { get; set; } = 14;

    // Colors.
    public Rgba Background { get; set; } = Rgba.White;
    public bool OceanEnabled { get; set; } = true;
    public Rgba Ocean { get; set; } = new(200, 220, 240);
    public Rgba Stroke { get; set; } = new(30, 30, 30);
    public Rgba Fill { get; set; } = new(70, 130, 180, 120);
    public Rgba LabelColor { get; set; } = new(20, 20, 20);
    public bool HaloEnabled { get; set; } = true;
    public Rgba LabelHalo { get; set; } = new(255, 255, 255, 200);

    /// <summary>The "knockout" backdrop painted under each label (off by default, like
    /// <see cref="RenderOptions.LabelKnockout"/>). Erases the geometry under the text instead of
    /// overlaying it.</summary>
    public bool KnockoutEnabled { get; set; }
    public Rgba LabelKnockout { get; set; } = Rgba.White;

    // Format-specific. PngCompression only affects a PNG write; SvgSimplifyTolerance only an SVG one.
    public CompressionLevel PngCompression { get; set; } = CompressionLevel.Optimal;
    public double SvgSimplifyTolerance { get; set; }
}
