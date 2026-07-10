/// <summary>
/// Recursion cap for the recursive-descent readers (WKT, WKB, KML, FlatGeobuf). Without it a hostile
/// input recurses until the stack overflows, which terminates the process and cannot be caught: a
/// megabyte of nested GEOMETRYCOLLECTIONs, &lt;MultiGeometry&gt;s or &lt;Folder&gt;s is enough, and a
/// FlatGeobuf part whose (signed) table offset points back at its own parent cycles forever. The
/// value matches <see cref="JsonDocument"/>'s default maximum depth, which is what already protects
/// the GeoJSON and TopoJSON codecs for free.
/// </summary>
static class Nesting
{
    public const int MaxDepth = 64;
}
