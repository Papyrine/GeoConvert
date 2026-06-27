namespace GeoConvert.App;

static class Program
{
    // The GEOCONVERT_SETTINGS environment variable overrides where settings live — used by tests and
    // screenshot tooling so they never touch the real per-user settings file (which gates the one-time
    // association prompt).
    static readonly SettingsManager settingsManager = new(
        Environment.GetEnvironmentVariable("GEOCONVERT_SETTINGS") is { Length: > 0 } path
            ? path
            : SettingsManager.DefaultSettingsPath);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return LaunchGui(() => new MainForm(settingsManager, null));
        }

        switch (args[0].ToLowerInvariant())
        {
            case "-h":
            case "--help":
            case "help":
                Cli.PrintUsage(Console.Out);
                return 0;
            case "--list":
            case "list":
                Cli.PrintFormats(Console.Out);
                return 0;
            case "associate":
                return Cli.Associate(Console.Out);
            case "unassociate":
                return Cli.Unassociate(Console.Out);
            case "settings":
                return Cli.PrintSettings(Console.Out, settingsManager);
            case "diff":
                return Diff(args[1..]);
        }

        // Not a subcommand. A lone existing file is the file-association double-click case: open it in the
        // window. Anything else is a usage error.
        if (args.Length == 1 && File.Exists(args[0]))
        {
            var file = args[0];
            return LaunchGui(() => new MainForm(settingsManager, file));
        }

        Cli.PrintUsage(Console.Error);
        return 2;
    }

    static int Diff(string[] diffArgs)
    {
        var code = Cli.ParseDiff(diffArgs, out var request, Console.Error);
        if (code != 0 || request == null)
        {
            return code;
        }

        // No output path => show the comparison in the window; otherwise render it headlessly.
        if (request.Output == null)
        {
            return LaunchGui(() => new DiffForm(request));
        }

        return Cli.ExecuteDiff(request, Console.Out, Console.Error);
    }

    static int LaunchGui(Func<Form> createForm)
    {
        // Launched from Explorer (a file-association double-click), a console-subsystem exe is handed its
        // own console window. Hide it so the windowed app presents cleanly. Launched from a terminal we
        // share the user's console and leave it be.
        if (NativeConsole.OwnsConsoleAlone())
        {
            NativeConsole.HideConsole();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(createForm());
        return 0;
    }
}
