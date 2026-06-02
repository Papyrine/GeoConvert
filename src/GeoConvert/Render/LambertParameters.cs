/// <summary>
/// Spherical Lambert Conformal Conic with two standard parallels picked from the input bounds.
/// Working on the unit sphere — earth radius drops out because the renderer applies a uniform
/// scale-to-fit afterwards — and the output is converted back to degree-equivalent units so the
/// envelope reads in the same scale as the other projections.
/// </summary>
sealed class LambertParameters
{
    // Reference longitude (radians) — the central meridian of the projection.
    readonly double lambda0;

    // How tightly the cone wraps the globe — the ratio between an angle on the unrolled cone and
    // the corresponding longitude span (so a 360° trip around the parallel becomes coneConstant·360°
    // on the flat map). Snyder's Working Manual calls this n; it's sin(φ₁) for a tangent cone, or
    // derived from both standard parallels for a secant cone. Sign follows the hemisphere: positive
    // for northern bounds (cone opens downward), negative for southern — signals which pole the
    // cone's apex points away from.
    readonly double coneConstant;

    // Radial scale of the cone — the numerator in ρ = coneScale / tan(π/4 + φ/2)^coneConstant,
    // controlling how far each parallel sits from the cone's apex. Snyder's Working Manual calls
    // this F; coneScale = cos(φ₁) · tan(π/4 + φ₁/2)^coneConstant / coneConstant.
    readonly double coneScale;

    // ρ at the reference parallel φ₀ — the "false northing" baseline so the origin maps to y = 0.
    readonly double rho0;

    LambertParameters(double lambda0, double coneConstant, double coneScale, double rho0)
    {
        this.lambda0 = lambda0;
        this.coneConstant = coneConstant;
        this.coneScale = coneScale;
        this.rho0 = rho0;
    }

    public static LambertParameters? TryFrom(Envelope bounds)
    {
        // Auto-pick standard parallels at the 1/6 and 5/6 marks of the data's latitude range — the
        // de facto convention used by national mapping agencies for country-scale LCC layouts. The
        // reference origin is the centre of the bounds.
        var minLat = bounds.MinY;
        var maxLat = bounds.MaxY;
        var span = maxLat - minLat;
        var phi1 = (minLat + span / 6) * Math.PI / 180;
        var phi2 = (maxLat - span / 6) * Math.PI / 180;
        var phi0 = (minLat + maxLat) / 2 * Math.PI / 180;
        var lambda0 = (bounds.MinX + bounds.MaxX) / 2 * Math.PI / 180;

        double coneConstant;
        if (Math.Abs(phi1 - phi2) < 1e-10)
        {
            // Single standard parallel (zero-height latitude span): cone tangent at φ₁.
            coneConstant = Math.Sin(phi1);
        }
        else
        {
            coneConstant = Math.Log(Math.Cos(phi1) / Math.Cos(phi2)) /
                Math.Log(Math.Tan(Math.PI / 4 + phi2 / 2) / Math.Tan(Math.PI / 4 + phi1 / 2));
        }

        // coneConstant → 0 means the cone has unfolded into a cylinder (bounds straddle the equator
        // symmetrically, or sit exactly on it); the LCC formulas degenerate and ρ blows up. Signal
        // the caller to fall back to a different projection rather than emit NaN pixels.
        if (!double.IsFinite(coneConstant) ||
            Math.Abs(coneConstant) < 1e-6)
        {
            return null;
        }

        var coneScale = Math.Cos(phi1) * Math.Pow(Math.Tan(Math.PI / 4 + phi1 / 2), coneConstant) / coneConstant;
        var rho0 = coneScale / Math.Pow(Math.Tan(Math.PI / 4 + phi0 / 2), coneConstant);
        return new(lambda0, coneConstant, coneScale, rho0);
    }

    public (double X, double Y) Project(double longitude, double latitude)
    {
        // Clamp away from the pole on the cone's opposite side, where tan(π/4 + φ/2) reaches 0 or
        // ∞ and ρ diverges. Sensible country-scale bounds never trip this; it's a defensive guard
        // against malformed input reaching the rasterizer.
        var phi = Math.Clamp(latitude, -89.999, 89.999) * Math.PI / 180;
        var lambda = longitude * Math.PI / 180;
        var rho = coneScale / Math.Pow(Math.Tan(Math.PI / 4 + phi / 2), coneConstant);
        var theta = coneConstant * (lambda - lambda0);
        var x = rho * Math.Sin(theta);
        var y = rho0 - rho * Math.Cos(theta);
        // Convert to degree-equivalent units (matches the WebMercator output unit) so the scale-to-
        // fit envelope reads in the same range as longitude. The ratio is preserved, so this only
        // affects how the projected coordinates *look* in the envelope, not the rendered aspect.
        return (x * 180 / Math.PI, y * 180 / Math.PI);
    }
}
