namespace GeoConvert.App;

/// <summary>
/// Console-window management for the hybrid GUI/CLI tool. The app is a console-subsystem exe (so CLI
/// subcommands print and return exit codes normally when run from a terminal). The cost is that an
/// Explorer double-click — a file association launch — allocates a fresh console window. When we detect
/// that case (we own the console alone) and we're about to show the GUI, we hide it so the windowed app
/// looks like a windowed app. Launched from a real terminal we leave the console alone.
/// </summary>
static class NativeConsole
{
    const int SwHide = 0;

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr handle, int command);

    /// <summary>
    /// True when this process is the only one attached to its console — the signature of a console
    /// freshly allocated for us by Explorer, rather than a terminal we were launched from (which has at
    /// least the shell attached too).
    /// </summary>
    public static bool OwnsConsoleAlone()
    {
        var processes = new uint[4];
        var count = GetConsoleProcessList(processes, (uint) processes.Length);
        return count == 1;
    }

    /// <summary>Hides this process's console window, if it has one.</summary>
    public static void HideConsole()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwHide);
        }
    }
}
