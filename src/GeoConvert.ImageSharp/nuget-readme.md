# GeoConvert.ImageSharp

A [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp)-backed PNG render backend for
[GeoConvert](https://www.nuget.org/packages/GeoConvert).

GeoConvert ships its own dependency-free software rasterizer. This optional package renders the same
scene — identical projection, per-layer styling, stroke auto-scaling and label placement — through
ImageSharp instead, trading the no-dependency guarantee for ImageSharp's antialiased fills and native
text.

```cs
using GeoConvert;
using GeoConvert.ImageSharp;

var collection = GeoConverter.Read("world.geojson");
ImageSharpRenderer.RenderPng(collection, "world.png", new()
{
    Width = 2048,
    Label = feature => feature.Properties.TryGetValue("name", out var value) ? value as string : null,
});
```

The API mirrors `MapRenderer.RenderPng` (single collection or a stacked list, to `byte[]`, a path, or
a `Stream`) and honours the same `RenderOptions`. Labels are drawn with an installed system sans-serif
font.

> **Licensing:** ImageSharp is distributed under the Six Labors Split License — free for open-source
> and personal use, but commercial use may require a paid license. See
> [sixlabors.com](https://sixlabors.com/pricing/). GeoConvert and GeoConvert.Skia carry no such terms.

See the [GitHub repo](https://github.com/Papyrine/GeoConvert) for full documentation.
