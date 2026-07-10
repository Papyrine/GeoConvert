// Direct tests over the internal Goode lobe splitter (Tests has InternalsVisibleTo). The renderer
// only ever asks "did it throw / did it paint", so the lobe sequence and split points a polyline
// gets cut into are asserted here instead.
public class GoodeLobesTests
{
    static int LobeIndex(GoodeLobes.Lobe lobe) =>
        Array.FindIndex(GoodeLobes.AllLobes, _ => _.Equals(lobe));

    static (int Lobe, (double X, double Y)[] Positions)[] Subdivide(params Position[] positions) =>
        GoodeLobes.SubdividePath(positions)
            .Select(_ => (LobeIndex(_.Lobe), _.Positions.Select(p => (p.X, p.Y)).ToArray()))
            .ToArray();

    [Test]
    public async Task Splits_at_the_Greenland_latitude_step()
    {
        // lon=-30 is east of the -40° cut below lat 60 (Eurasia lobe) and west of the -10° cut
        // above it (Americas lobe, thanks to the Greenland tab). So a due-north line at that
        // longitude changes lobe across a *latitude* boundary, with no shared meridian anywhere
        // in its lon span.
        var subpaths = Subdivide(new(-30, 55), new(-30, 65));

        await Assert.That(subpaths.Length).IsEqualTo(2);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(1);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(-30d, 55d), (-30d, 60d)]);
        await Assert.That(subpaths[1].Lobe).IsEqualTo(0);
        await Assert.That(subpaths[1].Positions).IsEquivalentTo([(-30d, 60d), (-30d, 65d)]);
    }

    [Test]
    public async Task Splits_a_segment_that_skips_over_a_lobe()
    {
        // A single segment spanning three southern lobes: it starts in the Pacific lobe
        // (-180..-100), passes entirely through the S-America lobe (-100..-20) without either
        // endpoint landing in it, and ends in the Africa lobe (-20..80).
        var subpaths = Subdivide(new(-150, -40), new(50, -40));

        await Assert.That(subpaths.Length).IsEqualTo(3);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(2);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(-150d, -40d), (-100d, -40d)]);
        await Assert.That(subpaths[1].Lobe).IsEqualTo(3);
        await Assert.That(subpaths[1].Positions).IsEquivalentTo([(-100d, -40d), (-20d, -40d)]);
        await Assert.That(subpaths[2].Lobe).IsEqualTo(4);
        await Assert.That(subpaths[2].Positions).IsEquivalentTo([(-20d, -40d), (50d, -40d)]);
    }

    [Test]
    public async Task Splits_a_westward_segment_that_skips_over_a_lobe()
    {
        // The mirror of the eastward case. Boundary planes are collected in ascending lon order,
        // so a westward segment meets them in descending parameter order and the crossing list has
        // to be sorted before the runs can be walked.
        var subpaths = Subdivide(new(50, -40), new(-150, -40));

        await Assert.That(subpaths.Select(_ => _.Lobe).ToArray()).IsEquivalentTo([4, 3, 2]);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(50d, -40d), (-20d, -40d)]);
        await Assert.That(subpaths[1].Positions).IsEquivalentTo([(-20d, -40d), (-100d, -40d)]);
        await Assert.That(subpaths[2].Positions).IsEquivalentTo([(-100d, -40d), (-150d, -40d)]);
    }

    [Test]
    public async Task Splits_at_a_vertex_that_sits_on_a_boundary()
    {
        // The middle vertex lands exactly on the -40° cut, so the segment leaving it crosses no
        // plane strictly — the lobe change shows up as the first run's midpoint instead, and the
        // vertex itself is the split point.
        var subpaths = Subdivide(new(-50, 30), new(-40, 30), new(-30, 30));

        await Assert.That(subpaths.Length).IsEqualTo(2);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(0);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(-50d, 30d), (-40d, 30d)]);
        await Assert.That(subpaths[1].Lobe).IsEqualTo(1);
        await Assert.That(subpaths[1].Positions).IsEquivalentTo([(-40d, 30d), (-30d, 30d)]);
    }

    [Test]
    public async Task Splits_a_segment_that_leaves_and_re_enters_the_same_lobe()
    {
        // The Americas lobe is L-shaped (the Greenland tab), so both endpoints can sit in it
        // while the straight lon/lat segment between them cuts across the Eurasia lobe's notch.
        var subpaths = Subdivide(new(-45, 10), new(-15, 62));

        await Assert.That(subpaths.Select(_ => _.Lobe).ToArray()).IsEquivalentTo([0, 1, 0]);
    }

    [Test]
    public async Task Crosses_a_lobe_corner_without_splitting()
    {
        // (-40, 60) is the corner where the Americas lobe's lon and lat boundaries meet. This
        // segment hits both planes at the same point, but stays inside the Americas lobe the
        // whole way (below the corner it's in the wide rect, above it in the Greenland tab), so
        // the coincident crossings must not manufacture a split.
        var subpaths = Subdivide(new(-50, 50), new(-30, 70));

        await Assert.That(subpaths.Length).IsEqualTo(1);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(0);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(-50d, 50d), (-30d, 70d)]);
    }

    [Test]
    public async Task Crosses_a_boundary_plane_interior_to_one_lobe_without_splitting()
    {
        // lon=-40 is a lobe boundary in the north but runs through the middle of the southern
        // S-America lobe (-100..-20). Crossing it there is not a lobe change.
        var subpaths = Subdivide(new(-60, -30), new(-30, -30));

        await Assert.That(subpaths.Length).IsEqualTo(1);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(3);
    }

    [Test]
    public async Task Starts_exactly_on_a_boundary_without_emitting_an_empty_subpath()
    {
        // FindLobe resolves the shared meridian -40 to the *first* matching lobe (Americas), but
        // the path immediately heads east into Eurasia. The zero-length Americas stub must not be
        // emitted.
        var subpaths = Subdivide(new(-40, 30), new(-30, 30));

        await Assert.That(subpaths.Length).IsEqualTo(1);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(1);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(-40d, 30d), (-30d, 30d)]);
    }

    [Test]
    public async Task Splits_at_the_equator()
    {
        var subpaths = Subdivide(new(0, 30), new(0, -30));

        await Assert.That(subpaths.Length).IsEqualTo(2);
        await Assert.That(subpaths[0].Lobe).IsEqualTo(1);
        await Assert.That(subpaths[0].Positions).IsEquivalentTo([(0d, 30d), (0d, 0d)]);
        await Assert.That(subpaths[1].Lobe).IsEqualTo(4);
        await Assert.That(subpaths[1].Positions).IsEquivalentTo([(0d, 0d), (0d, -30d)]);
    }

    [Test]
    public async Task Keeps_a_same_lobe_walk_in_one_subpath()
    {
        var subpaths = Subdivide(new(-25, 50), new(-30, 50), new(-35, 50));

        await Assert.That(subpaths.Length).IsEqualTo(1);
        await Assert.That(subpaths[0].Positions.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Ignores_a_degenerate_path() =>
        await Assert.That(Subdivide(new Position(0, 0)).Length).IsEqualTo(0);
}
