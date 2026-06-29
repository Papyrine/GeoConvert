using System.Diagnostics;

namespace GeoConvert.App;

/// <summary>
/// The Help ▸ About dialog. A plain form rather than a MessageBox, so it stays silent (the information
/// MessageBox plays a system sound) and can host a clickable link to the project. Auto-sizes to its
/// content so it lays out correctly at any display DPI.
/// </summary>
sealed class AboutForm : Form
{
    const string projectUrl = "https://github.com/Papyrine/GeoConvert";

    public AboutForm()
    {
        Text = "About GeoConvert";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // Not docked: a Dock=Fill panel inside an AutoSize form is circular (each defers to the other)
        // and resolves a few pixels short. Undocked + AutoSize, the form simply sizes to the panel.
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new(16),
        };

        var title = new Label
        {
            Text = "GeoConvert",
            AutoSize = true,
            Font = new(Font.FontFamily, Font.Size + 3, FontStyle.Bold),
            Margin = new(3, 3, 3, 8),
        };

        var description = new Label
        {
            Text =
                "Convert maps between GeoJSON, TopoJSON, Shapefile, FlatGeobuf, KML/KMZ, GPX, WKT, WKB,\n" +
                "CSV and GeoParquet; render to PNG/SVG; and compare two maps.",
            AutoSize = true,
            Margin = new(3, 3, 3, 10),
        };

        var link = new LinkLabel
        {
            Text = projectUrl,
            AutoSize = true,
            Margin = new(3, 3, 3, 14),
        };
        link.LinkClicked += (_, _) => OpenProject();

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new(3),
        };

        layout.Controls.Add(title);
        layout.Controls.Add(description);
        layout.Controls.Add(link);
        layout.Controls.Add(ok);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = ok;
    }

    static void OpenProject()
    {
        try
        {
            // UseShellExecute lets the OS open the URL in the default browser.
            Process.Start(new ProcessStartInfo(projectUrl) { UseShellExecute = true });
        }
        catch
        {
            // Opening a link is best-effort; if no handler is registered, do nothing rather than crash.
        }
    }
}
