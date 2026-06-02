readonly record struct ResolvedLabelStyle(
    Func<Feature, string?>? Label,
    double Size,
    Rgba Color,
    Rgba? Halo,
    Rgba? Knockout,
    Func<Feature, double>? Priority,
    double PointRadius);
