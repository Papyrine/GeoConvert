public class PixelSimplifierTests
{
    [Test]
    public async Task Returns_input_when_too_few_points()
    {
        var simplified = PixelSimplifier.Simplify([(0, 0), (10, 10)], 1);
        await Assert.That(simplified.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Drops_collinear_vertex_below_tolerance()
    {
        // The exactly-collinear midpoint sits 0 px off the chord, so it is dropped (and exercises the
        // farthest == -1 / distance-not-above-zero branch).
        var simplified = PixelSimplifier.Simplify([(0, 0), (5, 0), (10, 0)], 1);
        await Assert.That(simplified.Length).IsEqualTo(2);
        await Assert.That(simplified[0]).IsEqualTo((0d, 0d));
        await Assert.That(simplified[1]).IsEqualTo((10d, 0d));
    }

    [Test]
    public async Task Keeps_vertex_above_tolerance()
    {
        var simplified = PixelSimplifier.Simplify([(0, 0), (5, 5), (10, 0)], 1);
        await Assert.That(simplified.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Keeps_closed_ring_extent_via_degenerate_chord()
    {
        // First == last (a closed ring), so the initial chord is degenerate and distance falls back to
        // straight-line from the shared vertex — both interior corners stay.
        var simplified = PixelSimplifier.Simplify([(0, 0), (5, 5), (10, 0), (0, 0)], 1);
        await Assert.That(simplified.Length).IsEqualTo(4);
    }
}
