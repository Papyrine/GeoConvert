namespace GeoConvert.App;

static class SplitLayout
{
    /// <summary>
    /// Sizes a dock-filled <see cref="SplitContainer"/> so Panel2 ends up about <paramref name="panel2Width"/>
    /// pixels wide, clamping the splitter into its valid range. Call this once the container has its real
    /// size (e.g. from <see cref="Form.OnLoad"/>) — setting <see cref="SplitContainer.Panel2MinSize"/>
    /// while the container is still at its tiny construction-time default throws, because the default
    /// splitter distance then sits outside <c>[Panel1MinSize, Width - Panel2MinSize]</c>.
    /// </summary>
    public static void ConfigureSplit(SplitContainer split, int panel2Width)
    {
        if (split.Width <= 0)
        {
            return;
        }

        split.Panel2MinSize = Math.Min(panel2Width, Math.Max(80, split.Width / 3));
        var max = split.Width - split.Panel2MinSize;
        if (max < split.Panel1MinSize)
        {
            return;
        }

        split.SplitterDistance = Math.Clamp(split.Width - panel2Width, split.Panel1MinSize, max);
    }
}
