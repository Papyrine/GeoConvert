// Whether a label's anchor came from a point feature (anchor IS the feature; label should sit
// beside the dot, walking the Imhof candidate ring) or from a polygon/line interior (anchor is
// the centroid / midpoint; label should sit ON it). GeometryCollection inherits its kind from
// the first child that yields a usable anchor.
readonly record struct AnchorPoint(double X, double Y, AnchorKind Kind);
