namespace GeoConvert;

/// <summary>
/// The line-simplification algorithm used by <see cref="Simplifier"/>. The two have different
/// tolerance units — see <see cref="Simplifier.Simplify(Geometry, double, SimplifyMethod)"/>.
/// </summary>
public enum SimplifyMethod
{
    /// <summary>
    /// Ramer–Douglas–Peucker. Recursively keeps the vertex farthest from the chord between the
    /// segment's endpoints, dropping any vertex within <c>tolerance</c> perpendicular distance of the
    /// retained line. Tolerance is a <b>distance</b> in coordinate units (degrees for WGS84). Fast and
    /// the most widely used; preserves spikes and overall extent well but can leave visually uneven
    /// vertex spacing.
    /// </summary>
    DouglasPeucker,

    /// <summary>
    /// Visvalingam–Whyatt. Repeatedly removes the vertex whose "effective area" (the triangle formed
    /// with its two neighbours) is smallest, until the smallest remaining area reaches the threshold.
    /// Tolerance is an <b>area</b> in squared coordinate units (degrees² for WGS84). Tends to produce
    /// smoother, more evenly simplified outlines than Douglas–Peucker, at a slightly higher cost.
    /// </summary>
    Visvalingam,
}
