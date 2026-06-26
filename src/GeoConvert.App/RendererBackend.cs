namespace GeoConvert.App;

/// <summary>
/// Which PNG rasterizer to use, mirroring the geoconvert CLI's renderer flag. SVG always uses the
/// built-in vector writer regardless of this choice.
/// </summary>
public enum RendererBackend
{
    /// <summary>GeoConvert's dependency-free software rasterizer (<see cref="MapRenderer"/>).</summary>
    BuiltIn,

    /// <summary>SixLabors.ImageSharp-backed rasterizer (<see cref="ImageSharpRenderer"/>); labels use a system font.</summary>
    ImageSharp,
}
