namespace GeoConvert.App;

/// <summary>How a map diff is drawn.</summary>
public enum DiffMode
{
    /// <summary>Both maps drawn on one canvas in distinct colours, so shared geometry blends and
    /// differences stand out in pure A- or B-colour.</summary>
    Overlay,

    /// <summary>The two maps drawn separately at the same extent/scale and placed next to each other.</summary>
    SideBySide,
}
