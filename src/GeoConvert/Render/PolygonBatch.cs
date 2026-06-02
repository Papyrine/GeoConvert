/// <summary>One projected polygon piece: closed rings to fill (even-odd) plus the open
/// polylines to stroke. Fill and Strokes diverge for interrupted Goode, where the clipped
/// fill ring includes synthetic edges along the lobe boundary that the strokes omit.</summary>
readonly record struct PolygonBatch((double X, double Y)[][] Fill, (double X, double Y)[][] Strokes);
