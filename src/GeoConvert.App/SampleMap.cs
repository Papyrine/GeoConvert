namespace GeoConvert.App;

/// <summary>
/// The bundled sample world map — country borders, topology-simplified — the same map the Blazor app
/// ships. MapBundle stages it next to the app at build time as <c>maps/World/borders.fgb</c> (see the
/// csproj); this resolves that path at runtime, whether running from the build output or an installed
/// dotnet tool.
/// </summary>
static class SampleMap
{
    /// <summary>The bundled map's path, or null when it isn't present beside the app.</summary>
    public static string? Locate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "maps", "World", "borders.fgb");
        return File.Exists(path) ? path : null;
    }
}
