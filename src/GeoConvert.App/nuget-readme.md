# GeoConvert.App

A Windows desktop app — and .NET tool — that converts maps between geospatial formats, renders them to
PNG/SVG, and diffs two maps. It puts a GUI on top of [GeoConvert](https://www.nuget.org/packages/GeoConvert/)
and grows out of the same feature set as the GeoConvert Blazor sample.

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
- **Compare maps** (Tools ▸ Compare maps…) — an overlay of the two maps in distinct colours, or a
  side-by-side at a shared extent, alongside a structural summary.

### First run

On first launch the app offers to bind the supported map file types to itself (per-user, no admin), so
double-clicking a map opens it here. This can be changed any time from Tools ▸ Associate / Remove file
associations, or the `associate` / `unassociate` commands.

## Command line

The diff is scriptable headlessly:

```
geoconvert-app diff before.geojson after.geojson changes.png
geoconvert-app diff a.kml b.kml diff.png --mode side-by-side --size 1600 --projection lambert
```

With an output path the diff image is written and a summary printed; without one the comparison opens in
the window. Other commands: `associate`, `unassociate`, `settings`, `--list`, `--help`.
