# GeoConvert.Skia

A [SkiaSharp](https://github.com/mono/SkiaSharp)-backed PNG render backend for
[GeoConvert](https://www.nuget.org/packages/GeoConvert).

GeoConvert ships its own dependency-free software rasterizer. This optional package renders the same
scene — identical projection, per-layer styling, stroke auto-scaling and label placement — through
Skia instead, trading the no-dependency guarantee for Skia's antialiased fills and native text.

```cs
using GeoConvert;
using GeoConvert.Skia;

var collection = GeoConverter.Read("world.geojson");
SkiaRenderer.RenderPng(collection, "world.png", new()
{
    Width = 2048,
    Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
});
```

The API mirrors `MapRenderer.RenderPng` (single collection or a stacked list, to `byte[]`, a path, or
a `Stream`) and honours the same `RenderOptions`. Labels are drawn with Skia's default typeface.

See the [GitHub repo](https://github.com/SimonCropp/GeoConvert) for full documentation.
