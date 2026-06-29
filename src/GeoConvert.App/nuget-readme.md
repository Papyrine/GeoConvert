# GeoConvert.App

A Windows desktop app — and .NET tool — that converts maps between geospatial formats, renders them to
PNG/SVG, and diffs two maps. It puts a GUI on top of [GeoConvert](https://www.nuget.org/packages/GeoConvert/)
and grows out of the same feature set as the GeoConvert Blazor sample.

<img src="https://raw.githubusercontent.com/Papyrine/GeoConvert/main/src/GeoConvert.App.Tests/FormsTests.MainWindowWithMap.verified.png" width="900" alt="The GeoConvert.App main window with the sample world map loaded, showing the live preview and the render options column" />

## Install

```
dotnet tool install -g GeoConvert.App
```

This installs the `geoconvert-app` command (Windows, .NET 10 or later).

## The app

Run `geoconvert-app` (or open a map file with it) to launch the window:

- **Open** a GeoJSON, TopoJSON, Shapefile, FlatGeobuf, KML, KMZ, GPX, WKT, WKB, CSV or GeoParquet file
  and see a live preview.
- **Convert** to any supported format. For PNG/SVG output the full render options are exposed —
  projection, resolution, padding, strokes, point radius, stroke auto-scale, min-feature culling,
  labels (with halo / knockout), colours and ocean fill — plus the PNG renderer backend, PNG
  compression, SVG simplify tolerance, KMZ deflate level and GeoParquet codec.
- **Simplify** geometry as an optional pre-pass (Douglas–Peucker or Visvalingam, with a
  topology-preserving mode for shared borders).
- **Compare maps** (Tools ▸ Compare maps…) — see below.

The screenshot above has the bundled sample world map loaded (File ▸ Load sample world map); the
projection is on **Automatic**, which picks Goode Homolosine for a whole-world extent.

### First run

On first launch the app offers to bind the supported map file types to itself (per-user, no admin), so
double-clicking a map opens it here. This can be changed any time from Tools ▸ Associate / Remove file
associations, or the `associate` / `unassociate` commands.

## Comparing maps

**Tools ▸ Compare maps…** loads two maps and shows what changed, with a structural summary beside the
image — feature counts, geometry types, bounds, and which property keys each side carries. There are two
visual modes.

**Overlay** stacks both maps on one canvas in distinct colours (red for A, blue for B). Shared geometry
blends to purple, so anything left pure red was removed and anything pure blue was added:

<img src="https://raw.githubusercontent.com/Papyrine/GeoConvert/main/src/GeoConvert.App.Tests/FormsTests.DiffOverlay.verified.png" width="900" alt="The Compare maps window in Overlay mode: two maps stacked, with the A-only area in red, the B-only area in blue, and shared geometry in purple, plus the structural summary on the right" />

**Side by side** renders each map at the same shared extent so they line up for direct comparison:

<img src="https://raw.githubusercontent.com/Papyrine/GeoConvert/main/src/GeoConvert.App.Tests/FormsTests.DiffSideBySide.verified.png" width="900" alt="The Compare maps window in Side by side mode: map A on the left in red and map B on the right in blue, both at the same extent" />

## Command line

The diff is scriptable headlessly:

```
geoconvert-app diff before.geojson after.geojson changes.png
geoconvert-app diff a.kml b.kml diff.png --mode side-by-side --size 1600 --projection lambert
```

With an output path the diff image is written and a summary printed; without one the comparison opens in
the window. Other commands: `associate`, `unassociate`, `settings`, `--list`, `--help`.
