namespace GeoConvert.App.Tests;

/// <summary>
/// Renders a WinForms control to a <see cref="Bitmap"/> for Verify to snapshot. WinForms requires an STA
/// thread, so the control is created, laid out and drawn on a dedicated one; the resulting bitmap is
/// thread-agnostic and handed back. Forms are briefly shown off-screen so their <c>OnLoad</c> (e.g. the
/// diff window's splitter layout) runs before the draw; plain controls just get a handle and a layout
/// pass. Nothing is verified here — the caller passes the bitmap to Verify.
/// </summary>
static class WinFormsSnapshot
{
    static bool stylesEnabled;

    public static Bitmap Render(Func<Control> factory, int width, int height)
    {
        Bitmap? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureStyles();
                using var control = factory();
                if (control is Form form)
                {
                    // Off-screen + off-taskbar so the brief show is invisible; Show() raises OnLoad/OnShown
                    // so docked/split layouts settle before the draw.
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    form.Location = new(-5000, -5000);
                    form.Size = new(width, height);
                    form.Show();
                    Application.DoEvents();
                    result = Draw(form);
                    form.Close();
                }
                else
                {
                    control.Size = new(width, height);
                    _ = control.Handle;
                    control.PerformLayout();
                    Application.DoEvents();
                    result = Draw(control);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            throw failure;
        }

        return result!;
    }

    static Bitmap Draw(Control control)
    {
        var bounds = control.ClientRectangle;
        var bitmap = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        control.DrawToBitmap(bitmap, bounds);
        return bitmap;
    }

    static void EnsureStyles()
    {
        if (stylesEnabled)
        {
            return;
        }

        // Match the real app's themed rendering (ApplicationConfiguration.Initialize does this).
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        stylesEnabled = true;
    }
}
