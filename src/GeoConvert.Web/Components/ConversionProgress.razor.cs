namespace GeoConvert.Web.Components;

public partial class ConversionProgress
{
    /// <summary>Human-readable description of the current phase, e.g. "Rendering preview…".</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>The latest progress report, or null before any has arrived.</summary>
    [Parameter]
    public ConvertProgress? Report { get; set; }

    /// <summary>
    /// Forces the animated indeterminate bar even when <see cref="Report"/> carries a fraction. Used for
    /// the read phase: the source is already fully in memory, so its byte fraction hits 100% instantly
    /// and would otherwise freeze the bar through the (unreported) parse — an animated bar shows the work
    /// is ongoing, while the detail line still reports the feature/byte counts.
    /// </summary>
    [Parameter]
    public bool Indeterminate { get; set; }

    // Builds the "1,234 features · 1.2 / 3.4 MB" detail line from whatever the report carries. Each
    // count is omitted until it has moved, and a byte total (known on a seekable read) is shown as a
    // ratio. Returns null when there's nothing to show yet, so no detail span renders.
    string? Detail()
    {
        if (Report is not { } report)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (report.Features > 0)
        {
            parts.Add($"{report.Features:n0} {(report.Features == 1 ? "feature" : "features")}");
        }

        if (report.Bytes > 0)
        {
            parts.Add(report.ByteTotal is { } total
                ? $"{Humanize(report.Bytes)} / {Humanize(total)}"
                : Humanize(report.Bytes));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    static string Humanize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Invariant so the decimal point is always "." (the app runs under InvariantGlobalization, but
        // pin it here so the formatting is identical regardless of host culture).
        return unit == 0
            ? $"{bytes} B"
            : $"{size.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
