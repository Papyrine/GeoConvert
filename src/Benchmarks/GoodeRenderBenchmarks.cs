namespace GeoConvert.Benchmarks;

/// <summary>
/// Isolates the cost the interrupted <see cref="MapProjection.Goode"/> projection adds over a
/// non-interrupted one. Each pair renders the *same* world-spanning geometry at the same size with
/// deflate disabled (<see cref="CompressionLevel.NoCompression"/>), so the Goode − PlateCarree
/// delta is exactly the <c>GoodeLobes</c> work: Sutherland-Hodgman ring clipping
/// (<c>ClipRingWithTags</c>), polyline boundary subdivision (<c>SubdividePath</c> /
/// <c>InterpolateToBoundary</c>), and per-lobe projection.
/// <para>
/// Run before/after any GoodeLobes change to see whether a tweak actually moves the needle. The
/// MemoryDiagnoser <c>Allocated</c> column is the direct read on the LINQ question: the
/// <c>Lines_Goode</c> row is where <c>InterpolateToBoundary</c> fires, so its allocation delta over
/// <c>Lines_PlateCarree</c> shows what the SelectMany/First/Any churn actually costs — if it's a
/// rounding error against total render allocations, the LINQ there isn't worth hand-rolling.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class GoodeRenderBenchmarks
{
    // ~4-degree cells over the globe => thousands of small polygons, many straddling an interrupt
    // meridian or the equator, so the lobe clip runs on a large vertex count.
    FeatureCollection polygons = null!;

    // Globe-spanning polylines, one every 2 degrees of latitude, each crossing every lon interrupt
    // => the boundary-subdivision path runs at volume.
    FeatureCollection lines = null!;

    RenderOptions goode = null!;
    RenderOptions plateCarree = null!;

    [GlobalSetup]
    public void Setup()
    {
        polygons = SampleData.WorldPolygons(cellDegrees: 4);
        lines = SampleData.WorldLines(latStep: 2, verticesPerLine: 360);
        goode = Options(MapProjection.Goode);
        plateCarree = Options(MapProjection.PlateCarree);
    }

    static RenderOptions Options(MapProjection projection) =>
        new()
        {
            Width = 1024,
            Height = 512,
            Projection = projection,
            // Pin the extent to the whole world so both projections lay out identically and the
            // Goode path can't shortcut by clipping the data away.
            Bounds = new(-180, -90, 180, 90),
            // Strip deflate so the measured time is rasterisation + projection prep, not encoding.
            Png = new() { Compression = CompressionLevel.NoCompression },
        };

    // Non-interrupted baseline for the polygon workload — no lobe clipping happens.
    [Benchmark(Baseline = true)]
    public int Polygons_PlateCarree() =>
        MapRenderer.RenderPng(polygons, plateCarree).Length;

    // Same polygons through Goode — adds ClipRingWithTags + per-lobe projection. Delta over the
    // baseline is the polygon-side GoodeLobes cost.
    [Benchmark]
    public int Polygons_Goode() =>
        MapRenderer.RenderPng(polygons, goode).Length;

    // Non-interrupted baseline for the line workload — no boundary subdivision happens.
    [Benchmark]
    public int Lines_PlateCarree() =>
        MapRenderer.RenderPng(lines, plateCarree).Length;

    // Same lines through Goode — adds SubdividePath / InterpolateToBoundary. Delta over
    // Lines_PlateCarree (especially the Allocated column) is the boundary-crossing LINQ cost.
    [Benchmark]
    public int Lines_Goode() =>
        MapRenderer.RenderPng(lines, goode).Length;
}
