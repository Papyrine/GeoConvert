# todo

Findings from a bug/perf audit. Every item below was reproduced with a running repro, not just
read off the source. Ordered by priority: 1–2 hit ordinary data, 3–5 need adversarial input,
6–7 are performance.

## 1. Goode crashes on lines crossing the 60°N Greenland step

`src/GeoConvert/Render/GoodeLobes.cs:434` (`InterpolateToBoundary`)

The function only ever searches for a shared **meridian** between the two lobes:

```csharp
var meridian = lobeA.Rects
    .SelectMany(r => new[] { r.LonMin, r.LonMax })
    .First(m => m >= lo && m <= hi &&
        lobeB.Rects.Any(rb => rb.LonMin == m || rb.LonMax == m));
```

But the Americas and Eurasia lobes are also divided by a **latitude** step at 60°N — the Greenland
cut-out (`GoodeLobes.cs:41-56`). A segment crossing that step has no shared meridian in `[lo, hi]`,
so `.First()` throws `InvalidOperationException: Sequence contains no matching element`.

Reproduced:

```
[ THROW ] line lon=-30, lat 55->65 (crosses Greenland lat=60 step): InvalidOperationException
[ THROW ] line (-150,-40)->(50,-40) (skips a southern lobe):        InvalidOperationException
[ OK    ] line (-50,30)->(-30,30)  (adjacent lobes, shared meridian -40)
[ OK    ] line (-50,10)->(-50,-10) (equator crossing)
```

Two distinct triggers: any line crossing 60°N between lon −40° and −10° (Greenland's east coast,
Iceland, or just a graticule meridian drawn south-to-north), and any line whose endpoints land in
non-adjacent lobes.

Not opt-in. `Projection.Resolve` picks Goode whenever bounds span ≥180° lon or ≥90° lat
(`Projection.cs:554`), so a world-extent line dataset crashes under the **default**
`MapProjection.Auto`. Polygons are safe — they go through Sutherland–Hodgman clipping; only the
`SubdividePath` line path reaches this.

Existing tests cover adjacent-lobe and equator splits only (`PngTests.cs:703`, `:733`), which is
why it survived.

**Fix:** walk the segment lobe-by-lobe against both axes (a real multi-boundary split), or at
minimum fall back to the latitude boundary instead of `.First()` when no shared meridian exists.
Add tests for the lat=60 step and the lobe-skip case.

## 2. `SimplifyTopology` emits degenerate, spec-invalid rings

`src/GeoConvert/Internal/TopologySimplifier.cs:274` (`SimplifyRing`)

The plain path guards ring validity: `Simplifier.SimplifyPolygon` calls
`LineSimplifier.Simplify(ring, …, minPoints: 4)`, and a collapse below that falls back to
`MinimalRing`. The topology path bypasses that guard — it splits the ring at junctions, simplifies
each arc as an **open line** with `minPoints: 2`, then concatenates. Nothing re-checks the
reassembled ring.

Two triangles sharing an edge — the ordinary shared-border case this function exists to serve:

```
[DouglasPeucker] ring P -> 3 pts, 2 distinct: (0,0) (10,0) (0,0)
[Visvalingam]    ring P -> 3 pts, 2 distinct: (0,0) (10,0) (0,0)
plain Simplify   ring P -> 4 pts, 3 distinct: (0,0) (10,0) (5,0.01) (0,0)   <- correctly guarded
```

Signed area 0. It flows straight to output — `GeoJson.WriteString` emits a 3-position linear ring
(RFC 7946 §3.1.6 requires four or more) and WKT emits `POLYGON ((0 0, 10 0, 0 0))`.

**Fix:** after reassembling the arcs in `SimplifyRing`, validate the ring has ≥4 positions / ≥3
distinct vertices and fall back to `MinimalRing` (or retain the unsimplified arc) when it doesn't.
Affects both `DouglasPeucker` and `Visvalingam`.

## 3. Nested WKT kills the process (uncatchable stack overflow)

`src/GeoConvert/Internal/WktParser.cs:113-119`

`ParseTagged` → `ReadGeometryCollection` → `ParseTagged` has no depth cap.

```
depth  1000: parsed OK
depth 50000: Stack overflow.   <- process terminated, uncatchable
```

GeoJSON and TopoJSON are incidentally protected by `JsonDocument`'s default 64-level max depth.
WKT has nothing. Reachable from any `.wkt` input and from a WKT cell in a CSV
(`Csv.cs:68` → `Wkt.ParseGeometry`). `Kml.ReadMultiGeometry` (`Kml.cs:204`) has the same class of
issue on nested `<MultiGeometry>`.

**Fix:** thread a depth counter through `ParseTagged`/`ReadGeometryCollection` and throw
`GeoConvertException` past a cap (64, to match `JsonDocument`).

## 4. A 32-byte `.dbf` allocates 8 GiB

`src/GeoConvert/Internal/Dbf.cs:28` and `:65`

`recordCount` is read straight from the file and presizes the row list with no cross-check against
the bytes actually present:

```csharp
var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));  // :28
...
var rows = new List<object?[]>((int)recordCount);                           // :65
```

Measured, from a 32-byte input with no field terminator:

```
recordCount=0x40000000: IndexOutOfRangeException after allocating 8,192.0 MB
recordCount=0xFFFFFFFF: ArgumentOutOfRangeException (negative capacity)
```

**Fix:** bound `recordCount` against `(length - headerLength) / recordLength` before presizing.
Also bounds-check the per-cell `data.AsSpan(position, field.Length)` read at `:75`.

Same unvalidated-length-presizes-a-buffer pattern (caught, so DoS rather than corruption, but still
amplifies a few bytes into multi-GB allocations):
- `Formats/Wkb.cs:202-203` (`ReadCoordinates`), `:190-191` (`ReadRings`), `:131,142,153,164`
- `Formats/FlatGeobuf.cs:221` (`ReadProperties`), `:168`
- `Formats/GeoParquet.cs:89`, `:156` (`NumRows`)

`Snappy.Decompress` (`Snappy.cs:43`) already does this correctly with its 64× bound — use it as the
model.

## 5. Exception contract violations

CLAUDE.md states errors are surfaced as `GeoConvertException`. These leak raw BCL exceptions:

- `Formats/GeoJson.cs:124-138` — `{"type":"Point","coordinates":[1]}` → `InvalidOperationException`
- `Formats/GeoJson.cs:108` — `{"type":"Point"}` → `KeyNotFoundException`
- `Formats/TopoJson.cs:230-243`, `:245-253` — same shape
- `Formats/Shapefile.cs` / `Internal/Dbf.cs` — the only read path with no `GeoConvertException`
  wrapper at all (see #4)
- `Formats/Shapefile.cs:151-159` — `contentWords * 2` overflows to negative, the
  `position + contentBytes > length` guard passes, and `data.AsSpan(position, contentBytes)` throws
  raw

WKB wraps correctly (verified) — KML/GPX do too. GeoJSON/TopoJSON/Shapefile are the gaps.

## 6. Polygon fill is O(edges × rows)

`src/GeoConvert/Internal/Canvas.cs:551-570` (`FillScanline`)

Every output row re-walks every edge of every ring, ×4 sub-samples, including edges nowhere near
that row. Measured in **Release**, single polygon, **fixed** 800×600 canvas — row count is constant,
so all growth is the edge walk:

| vertices | median ms |
|---------:|----------:|
|      500 |      12.9 |
|    2,000 |      14.1 |
|    8,000 |      18.8 |
|   32,000 |      32.3 |
|  128,000 |      72.1 |

~0.44 ms per 1,000 vertices. At 128k vertices roughly 59 ms of the 72 ms total is edge walking
(~82%). `Parallel.For` (`Canvas.cs:504`) spreads this across cores but does not reduce it.

**Fix:** active-edge table — bucket edges by their minimum row, maintain the active set as rows
advance. Per-row work becomes proportional to *active* edges (~2 for a convex ring) instead of all
of them. Structural change, not a tweak. Largest single win available.

## 7. Smaller perf wins

- `Render/Projection.cs:109` — `rings.Select(ToPixels).ToArray()` allocates a method-group delegate
  + a `Select` iterator + (via `yield`) a state machine, per polygon, on the non-Goode hot path.
  A manual `for` loop returning a single `PolygonBatch` removes all three.
- `Formats/TopoJson.cs:186`, `:189`, `:221`, `:158` — decode lists grow by doubling.
  `JsonElement.GetArrayLength()` is O(1) and `GeoJson.cs:144` already pre-sizes for exactly this
  reason. TopoJSON is the Natural-Earth-scale format, so it pays the most churn while being the one
  place that skips the optimization.
- `Formats/FlatGeobuf.cs:119`, `:208-211` — the read path allocates 3 objects per feature
  (`GetByteVector`'s `ToArray` copy, a `MemoryStream`, a `BinaryReader`). The write path was already
  optimized to avoid this (the reused `propertyBuffer` at `:281`/`:449`). Read the properties in
  place from the backing array via `BinaryPrimitives`.

## Audited and clean — don't re-check

- **Culture sensitivity.** Every numeric parse/format site (KML, GPX, WKT, CSV, SVG, DBF, Scalars,
  JsonValue) passes `InvariantCulture`; JSON goes through `Utf8JsonWriter`/`JsonElement`, invariant
  by construction. (`Position.ToString()` is culture-sensitive but is debug-only, on no
  serialization path.)
- **Stream ownership.** Every writer/reader wraps the caller's stream with `leaveOpen: true`.
- **KMZ.** Read never extracts to disk, so no Zip-Slip. Disposal order is correct.
- **Snappy.** Tag/literal/length decoding correct; `CopyBack`'s byte-by-byte forward copy is the
  correct LZ77 overlap semantics.
- **Thrift compact.** Field-delta, bool-as-type-nibble, list-header `nibble==15` boundary, zigzag,
  stop-field all correct.
- **FlatBuffers.** `Prep` alignment, vtable soffset back-patching, offset math match the canonical
  algorithm.
- **Rasterizer blending.** The SIMD translucent-span path matches the scalar path bit-for-bit.
  Even-odd half-open crossing rule and 4-sub-sample coverage sum interior pixels to exactly 1.0.
  `AddSpan` clipping keeps all writes in bounds.
- **NaN/Infinity.** The projections all clamp (WebMercator ±85.0511°, Goode/Lambert ±89.999°), so
  non-finite values can't reach the rasterizer. Minor asymmetry: `SvgSurface.cs:182` would emit
  `"NaN"` given a non-finite input coordinate *plus* an explicit `RenderOptions.Bounds` (which
  bypasses the `Envelope.IsEmpty` check); the PNG path silently drops it instead.
- **Douglas–Peucker / Visvalingam.** Both use an explicit `Stack`, not recursion — no
  stack-overflow exposure. Squared-distance use is consistent; degenerate zero-length chords are
  handled. Visvalingam's lazy-deletion stale check is sound.
- **`Ring.SignedArea`.** The `% ring.Count` wrap makes shoelace correct for closed and unclosed
  rings alike.
- **`Position`.** A `readonly record struct`, so the junction `HashSet<Position>` gets value
  equality with no boxing.
- **PNG.** Row buffers allocated once, not per row; CRC covers type+data correctly.
- **Shapefile writer.** Endianness, 1-based record numbers, 16-bit-word content lengths, `.shx`
  offsets all correct.

## Lower confidence — worth a look, not verified

- `Internal/LineSimplifier.cs:123` — DP's strict `>` keeps the lowest-index farthest vertex, so on
  an exact perpendicular-distance **tie** the reversed traversal of a shared arc keeps the mirror
  vertex. That breaks TopologySimplifier's bit-identical-shared-arc guarantee and reintroduces the
  hairline gap the module exists to eliminate. Measure-zero on real lon/lat, plausible on
  gridded/quantised coordinates. `Visvalingam` has the same exposure via `PriorityQueue`'s unstable
  tie order (`:192`).
- `FeatureCollection.cs:32-44` — `Count` walks the whole subtree on every call; `GetBounds` (`:56`)
  and the recursive enumerator (`:72`) likewise. Currently every caller hits them once, but any
  `for (i = 0; i < fc.Count; i++)` is silently O(n²). Worth documenting as O(n) or caching.
- `FeatureCollection.Children` is a public mutable `List<FeatureCollection>` with no cycle
  protection. `fc.Children.Add(fc)` makes `Count`/`GetEnumerator`/`GetBounds`/`Simplifier` recurse
  until `StackOverflowException` (uncatchable).
- `Internal/Dbf.cs:417` + `:250-268` — a `long` property ≥ 10^18 is silently truncated to the
  18 most-significant digits on shapefile write (`FitNumeric` copies only `destination.Length`
  chars).
- `Internal/PixelSimplifier.cs:12-66` — no degenerate-ring guard, unlike `LineSimplifier`. A closed
  ring entirely within tolerance collapses to two coincident points (an empty `M…L` path) rather
  than a minimal sliver. Probably fine for pixel-space SVG; flagging the inconsistency.
- `Formats/Kml.cs:57-68` — a second top-level `<Document>` directly under `<kml>` merges into the
  root and overwrites `target.Name` rather than becoming a child layer, because `isRoot` stays true
  across siblings.
- `Formats/Wkt.cs:15-25` and `Formats/Csv.cs:40-50` fully materialize the input (`ReadToEnd`, then
  `Split('\n')` / a complete `List<List<string>>`). Both formats are line-oriented and could stream.
  Memory scales with file size — a design tradeoff, not a bug.
