namespace GeoConvert.App;

/// <summary>
/// The command-line surface of the app, hand-rolled in the same spirit as the geoconvert CLI's
/// <c>Runner</c> (no third-party parser). It owns the headless commands — <c>diff</c>, the file
/// association management, and <c>settings</c> — and the usage text. Interactive conversion/rendering is
/// the GUI's job; the CLI deliberately mirrors only the diff feature plus app management.
/// </summary>
public static class Cli
{
    /// <summary>A fully-parsed <c>diff</c> invocation. <see cref="Output"/> null means "open the diff in
    /// the GUI" rather than render headlessly.</summary>
    public sealed record DiffRequest(
        string PathA,
        string PathB,
        string? Output,
        RenderSettings Settings,
        DiffMode Mode,
        Rgba ColorA,
        Rgba ColorB);

    /// <summary>
    /// Parses the arguments after <c>diff</c>. Returns 0 with <paramref name="request"/> set on success,
    /// or 2 on a usage error (message already written to <paramref name="error"/>).
    /// </summary>
    public static int ParseDiff(string[] args, out DiffRequest? request, TextWriter error)
    {
        request = null;
        var settings = new RenderSettings();
        var mode = DiffMode.Overlay;
        var colorA = MapDiff.DefaultColorA;
        var colorB = MapDiff.DefaultColorB;
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--mode":
                    if (!TryNext(args, ref i, error, "--mode", out var modeText))
                    {
                        return 2;
                    }

                    if (!TryParseMode(modeText, out mode))
                    {
                        error.WriteLine("--mode must be 'overlay' or 'side-by-side'.");
                        return 2;
                    }

                    break;
                case "--color-a":
                    if (!TryNext(args, ref i, error, "--color-a", out var colorAText) ||
                        !RequireColor(colorAText, "--color-a", error, out colorA))
                    {
                        return 2;
                    }

                    break;
                case "--color-b":
                    if (!TryNext(args, ref i, error, "--color-b", out var colorBText) ||
                        !RequireColor(colorBText, "--color-b", error, out colorB))
                    {
                        return 2;
                    }

                    break;
                case "--bbox":
                    if (!TryNext(args, ref i, error, "--bbox", out var bboxText))
                    {
                        return 2;
                    }

                    if (!TryParseBounds(bboxText, out var bounds))
                    {
                        error.WriteLine("--bbox must be 'minX,minY,maxX,maxY'.");
                        return 2;
                    }

                    settings.Bounds = bounds;
                    break;
                case "--size":
                    if (!TryNext(args, ref i, error, "--size", out var sizeText))
                    {
                        return 2;
                    }

                    if (!TryParseSize(sizeText, out var width, out var height))
                    {
                        error.WriteLine("--size must be 'WIDTH' or 'WIDTHxHEIGHT'.");
                        return 2;
                    }

                    // An explicit pixel size overrides the default fit-to-box.
                    settings.MaxDimension = 0;
                    settings.Width = width;
                    settings.Height = height;
                    break;
                case "--max-dimension":
                    if (!TryNext(args, ref i, error, "--max-dimension", out var maxText))
                    {
                        return 2;
                    }

                    if (!int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxDimension) || maxDimension <= 0)
                    {
                        error.WriteLine("--max-dimension must be a positive integer (pixels).");
                        return 2;
                    }

                    settings.MaxDimension = maxDimension;
                    break;
                case "--projection":
                    if (!TryNext(args, ref i, error, "--projection", out var projectionText))
                    {
                        return 2;
                    }

                    if (!TryParseProjection(projectionText, out var projection))
                    {
                        error.WriteLine("--projection must be 'auto', 'plate-carree', 'web-mercator', 'lambert', or 'goode'.");
                        return 2;
                    }

                    settings.Projection = projection;
                    break;
                case "--renderer":
                    if (!TryNext(args, ref i, error, "--renderer", out var rendererText))
                    {
                        return 2;
                    }

                    if (!TryParseRenderer(rendererText, out var renderer))
                    {
                        error.WriteLine("--renderer must be 'builtin', 'skia', or 'imagesharp'.");
                        return 2;
                    }

                    settings.Renderer = renderer;
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        error.WriteLine($"Unknown option '{argument}'.");
                        return 2;
                    }

                    positionals.Add(argument);
                    break;
            }
        }

        if (positionals.Count is < 2 or > 3)
        {
            error.WriteLine("Usage: geoconvert-app diff <map1> <map2> [output.png] [options]");
            return 2;
        }

        var output = positionals.Count == 3 ? positionals[2] : null;
        request = new(positionals[0], positionals[1], output, settings, mode, colorA, colorB);
        return 0;
    }

    /// <summary>Runs a headless diff: renders the diff image to <see cref="DiffRequest.Output"/> and prints the summary.</summary>
    public static int ExecuteDiff(DiffRequest request, TextWriter output, TextWriter error)
    {
        if (!File.Exists(request.PathA))
        {
            error.WriteLine($"Input file not found: {request.PathA}");
            return 1;
        }

        if (!File.Exists(request.PathB))
        {
            error.WriteLine($"Input file not found: {request.PathB}");
            return 1;
        }

        try
        {
            var a = GeoConverter.Read(request.PathA);
            var b = GeoConverter.Read(request.PathB);

            output.Write(MapDiff.Summarize(Path.GetFileName(request.PathA), a, Path.GetFileName(request.PathB), b));

            if (request.Output is { } destination)
            {
                var image = MapDiff.Render(a, b, request.Settings, request.Mode, request.ColorA, request.ColorB);
                File.WriteAllBytes(destination, image);
                output.WriteLine();
                output.WriteLine($"Diff image ({request.Mode}) written to {destination}.");
            }

            return 0;
        }
        catch (GeoConvertException exception)
        {
            error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    public static int Associate(TextWriter output)
    {
        FileAssociations.Associate();
        output.WriteLine("Bound the supported map formats to GeoConvert:");
        output.WriteLine($"  {string.Join(" ", FileAssociations.Extensions)}");
        return 0;
    }

    public static int Unassociate(TextWriter output)
    {
        FileAssociations.Unassociate();
        output.WriteLine("Removed GeoConvert's map file associations.");
        return 0;
    }

    public static int PrintSettings(TextWriter output, SettingsManager settingsManager)
    {
        output.WriteLine(settingsManager.SettingsPath);
        if (File.Exists(settingsManager.SettingsPath))
        {
            output.WriteLine(File.ReadAllText(settingsManager.SettingsPath));
        }
        else
        {
            output.WriteLine("No settings file found.");
        }

        output.WriteLine($"File associations bound: {(FileAssociations.IsAssociated() ? "yes" : "no")}");
        return 0;
    }

    // --- parsing helpers (shared shapes with the geoconvert CLI's Runner) ---

    static bool TryNext(string[] args, ref int index, TextWriter error, string option, out string value)
    {
        if (index + 1 >= args.Length)
        {
            error.WriteLine($"Missing value for {option}.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    static bool RequireColor(string text, string option, TextWriter error, out Rgba color)
    {
        if (TryParseColor(text, out color))
        {
            return true;
        }

        error.WriteLine($"{option} must be '#RRGGBB' or '#RRGGBBAA'.");
        return false;
    }

    static bool TryParseMode(string text, out DiffMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "overlay":
                mode = DiffMode.Overlay;
                return true;
            case "side-by-side":
            case "sidebyside":
            case "side":
                mode = DiffMode.SideBySide;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    static bool TryParseBounds(string text, out Envelope bounds)
    {
        bounds = default;
        var parts = text.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        bounds = new(values[0], values[1], values[2], values[3]);
        return true;
    }

    static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = text.Split('x', 'X');
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
        {
            return false;
        }

        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0))
        {
            return false;
        }

        return true;
    }

    static bool TryParseProjection(string text, out MapProjection projection)
    {
        switch (text.ToLowerInvariant())
        {
            case "auto":
            case "automatic":
                projection = MapProjection.Auto;
                return true;
            case "plate-carree":
            case "platecarree":
            case "equirectangular":
                projection = MapProjection.PlateCarree;
                return true;
            case "web-mercator":
            case "webmercator":
            case "mercator":
                projection = MapProjection.WebMercator;
                return true;
            case "lambert":
            case "lambert-conformal":
            case "lambert-conformal-conic":
            case "lcc":
                projection = MapProjection.Lambert;
                return true;
            case "goode":
            case "homolosine":
            case "goode-homolosine":
                projection = MapProjection.Goode;
                return true;
            default:
                projection = default;
                return false;
        }
    }

    static bool TryParseRenderer(string text, out RendererBackend renderer)
    {
        switch (text.ToLowerInvariant())
        {
            case "builtin":
            case "built-in":
            case "default":
                renderer = RendererBackend.BuiltIn;
                return true;
            case "skia":
            case "skiasharp":
                renderer = RendererBackend.Skia;
                return true;
            case "imagesharp":
            case "image-sharp":
            case "sixlabors":
                renderer = RendererBackend.ImageSharp;
                return true;
            default:
                renderer = default;
                return false;
        }
    }

    static bool TryParseColor(string text, out Rgba color)
    {
        color = default;
        if (text.Length < 7 || text[0] != '#')
        {
            return false;
        }

        var hex = text.AsSpan(1);
        if (hex.Length != 6 && hex.Length != 8)
        {
            return false;
        }

        if (!byte.TryParse(hex.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        byte a = 255;
        if (hex.Length == 8 &&
            !byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
        {
            return false;
        }

        color = new(r, g, b, a);
        return true;
    }

    public static void PrintUsage(TextWriter writer) =>
        writer.WriteLine(
            """
            geoconvert-app - a desktop map converter, renderer and diff tool.

            Run with no arguments to open the window. Pass a map file to open it directly.

            Usage:
              geoconvert-app                         Open the app.
              geoconvert-app <file>                  Open the app with a map loaded.
              geoconvert-app diff <map1> <map2> [output.png] [options]
                                                     Compare two maps. With an output path the diff is
                                                     rendered headlessly and a summary is printed; without
                                                     one the diff opens in the window.
              geoconvert-app associate               Bind the supported map formats to this app.
              geoconvert-app unassociate             Remove those file associations.
              geoconvert-app settings                Show the settings file and association state.
              geoconvert-app --list                  List supported formats.
              geoconvert-app --help                  Show this help.

            diff options:
              --mode overlay|side-by-side  Overlay both maps on one canvas (default) or place them
                                           side by side at a shared extent.
              --color-a <#hex>             Colour for the first map (default red).
              --color-b <#hex>             Colour for the second map (default blue).
              --bbox minX,minY,maxX,maxY   Extent to render (defaults to the union of both maps).
              --size WIDTH[xHEIGHT]        Image size in pixels.
              --max-dimension <pixels>     Cap the longer edge at this many pixels (fit-to-box).
              --projection <name>          auto | plate-carree | web-mercator | lambert | goode.
              --renderer <name>            builtin (default), skia or imagesharp (PNG only).

            Examples:
              geoconvert-app world.geojson
              geoconvert-app diff before.geojson after.geojson changes.png
              geoconvert-app diff a.kml b.kml diff.png --mode side-by-side --size 1600
            """);

    public static void PrintFormats(TextWriter writer) =>
        writer.WriteLine(
            """
            Supported formats:
              geojson    .geojson .json     (read/write)
              topojson   .topojson          (read/write)
              shapefile  .shp (+ .shx .dbf .prj)  (read/write)
              flatgeobuf .fgb               (read/write)
              kml        .kml               (read/write)
              kmz        .kmz               (read/write)
              gpx        .gpx               (read/write)
              wkt        .wkt               (read/write)
              wkb        .wkb               (read/write)
              csv        .csv               (read/write)
              geoparquet .parquet .geoparquet  (read/write)
              png        .png               (render output)
              svg        .svg               (render output)
            """);
}
