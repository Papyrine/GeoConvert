namespace GeoConvert.App;

/// <summary>
/// Optional lossy vertex reduction applied before writing or rendering — the GUI/CLI surface of
/// <see cref="Simplifier"/>. Off by default; when on, the loaded features are thinned (a new graph, the
/// original is untouched) using the chosen <see cref="Method"/> and tolerance, with
/// <see cref="Topology"/> switching to the shared-boundary variant so adjacent polygons stay joined.
/// </summary>
public sealed class SimplifySettings
{
    public bool Enabled { get; set; }
    public double Tolerance { get; set; } = 0.01;
    public SimplifyMethod Method { get; set; } = SimplifyMethod.DouglasPeucker;
    public bool Topology { get; set; }

    /// <summary>Returns <paramref name="collection"/> thinned per these settings, or unchanged when off.</summary>
    public FeatureCollection Apply(FeatureCollection collection)
    {
        if (!Enabled || Tolerance <= 0)
        {
            return collection;
        }

        return Topology
            ? Simplifier.SimplifyTopology(collection, Tolerance, Method)
            : Simplifier.Simplify(collection, Tolerance, Method);
    }
}
