/// <summary>
/// Renders a WinForms control to a <see cref="Bitmap"/> for Verify to snapshot. WinForms requires an STA
/// thread, so the control is created, laid out and drawn on a dedicated one; the resulting bitmap is
/// thread-agnostic and handed back. Forms are briefly shown off-screen so their <c>OnLoad</c> (e.g. the
/// diff window's splitter layout) runs before the draw; plain controls just get a handle and a layout
/// pass. Nothing is verified here — the caller passes the bitmap to Verify.
/// <para>
/// A <c>scale</c> &gt; 1 simulates a higher display DPI: WinForms applies DPI scaling through
/// <see cref="Control.Scale(SizeF)"/>, so calling it directly reproduces the same scaling behaviour
/// (including the class of bug where fixed-pixel sizes don't scale while font-sized controls do) without
/// needing an actual high-DPI monitor. scale 1.25 == 120 DPI / 125%, scale 1.5 == 144 DPI / 150%.
/// </para>
/// </summary>
static class WinFormsSnapshot
{
    static bool stylesEnabled;

    public static Bitmap Render(Func<Control> factory, int width, int height, float scale = 1f)
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
                    // An AutoSize form sizes itself to its content — forcing a size would clip it.
                    if (!form.AutoSize)
                    {
                        form.Size = new(width, height);
                    }

                    Rescale(form, scale);
                    form.Show();
                    Application.DoEvents();
                    result = Draw(form);
                    form.Close();
                }
                else
                {
                    control.Size = new(width, height);
                    _ = control.Handle;
                    Rescale(control, scale);
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

    /// <summary>
    /// Drives an async form operation (a load/save) to completion on the STA thread the form lives on, then
    /// projects the settled form state with <paramref name="select"/>. The WinForms
    /// <see cref="SynchronizationContext"/> is installed up-front — before the form is built — so the form's
    /// <see cref="Progress{T}"/> (captured in its constructor) and every awaited continuation marshal back
    /// to this thread, where the pump loop runs them; without it they'd post to the thread pool and touch
    /// controls cross-thread. Use this to snapshot a form *after* an operation has finished (e.g. that a
    /// completed load leaves a "Loaded …" status, not a stuck "Reading …"), not to draw it mid-flight.
    /// </summary>
    /// <summary>
    /// Like <see cref="RunToCompletion"/> but projects the settled form as a <see cref="Bitmap"/> — drives
    /// an async load/render to completion, then snapshots the populated window. This is how the documented
    /// "diff loaded" / "map loaded" states are captured: deterministically, through the same draw path as
    /// the empty-window snapshots (so the non-client-frame fix applies), rather than a live screen grab.
    /// </summary>
    public static Bitmap RenderAfter<TForm>(
        Func<TForm> factory,
        Func<TForm, Task> operation,
        int width,
        int height,
        float scale = 1f)
        where TForm : Form =>
        RunToCompletion(factory, operation, form => Draw(form), width, height, scale);

    public static TResult RunToCompletion<TForm, TResult>(
        Func<TForm> factory,
        Func<TForm, Task> operation,
        Func<TForm, TResult> select,
        int width,
        int height,
        float scale = 1f)
        where TForm : Form
    {
        TResult result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureStyles();
                if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                }

                using var form = factory();
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                form.Location = new(-5000, -5000);
                if (!form.AutoSize)
                {
                    form.Size = new(width, height);
                }

                Rescale(form, scale);
                form.Show();
                Application.DoEvents();

                var task = operation(form);
                // Pump the message loop so the posted continuations run on this thread until the operation
                // settles; the spin cap turns a regression that hangs into a fast failure rather than a hung
                // test run.
                for (var spins = 0; !task.IsCompleted; spins++)
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                    if (spins > 10_000)
                    {
                        throw new TimeoutException("The form operation did not complete within the expected time.");
                    }
                }

                task.GetAwaiter().GetResult(); // surface a failed load/save as a test failure
                Application.DoEvents();         // drain the final posted continuation(s)
                result = select(form);
                form.Close();
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

        return result;
    }

    static void Rescale(Control control, float scale)
    {
        if (scale != 1f)
        {
            control.Scale(new SizeF(scale, scale));
        }
    }

    static Bitmap Draw(Control control)
    {
        // A top-level Form draws its non-client frame (title bar + borders) into the bitmap, so the target
        // must span the whole window. ClientRectangle excludes the frame, which clipped the right and
        // bottom borders off every form snapshot (most visibly on the small About dialog, where the OK
        // button sits in the corner). A hosted child control has no non-client area, so its client
        // rectangle already is its whole surface.
        var bounds = control is Form
            ? new(0, 0, control.Width, control.Height)
            : control.ClientRectangle;
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

        // Deliberately do NOT call Application.EnableVisualStyles(). Themed (Aero) rendering delegates radio
        // buttons, check boxes and push buttons to the OS theme engine, which paints them differently from
        // one desktop session to the next — a blue themed radio dot on an interactive desktop vs a black
        // classic dot on a headless/CI agent — so the snapshots flapped purely on which machine ran them.
        // Classic (non-visual-styles) controls are drawn by fixed GDI code against the standard system
        // palette, identically everywhere; these tests exist to catch DPI/layout breaks, which classic
        // rendering still shows. SetCompatibleTextRenderingDefault stays so text metrics match the app.
        Application.SetCompatibleTextRenderingDefault(false);
        stylesEnabled = true;
    }
}
