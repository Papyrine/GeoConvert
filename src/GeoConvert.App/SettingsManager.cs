namespace GeoConvert.App;

/// <summary>
/// Reads and writes <see cref="Settings"/> as indented JSON. Modelled on MsOfficeDiff's settings
/// manager, but synchronous and dependency-free (System.Text.Json, no logging package): a read failure
/// degrades to defaults rather than throwing, so a corrupt file never blocks startup.
/// </summary>
public class SettingsManager
{
    readonly string settingsPath;

    public SettingsManager(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        this.settingsPath = settingsPath;
    }

    static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string DefaultSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "GeoConvert",
        "settings.json");

    public string SettingsPath => settingsPath;

    public Settings Read()
    {
        if (!File.Exists(settingsPath))
        {
            return new();
        }

        try
        {
            using var stream = File.OpenRead(settingsPath);
            return JsonSerializer.Deserialize<Settings>(stream) ?? new();
        }
        catch
        {
            // A malformed settings file is not worth failing startup over — fall back to defaults. The
            // next Write rewrites it cleanly.
            return new();
        }
    }

    public void Write(Settings settings)
    {
        using var stream = File.Create(settingsPath);
        JsonSerializer.Serialize(stream, settings, jsonOptions);
    }

    /// <summary>Reads the settings, applies <paramref name="mutate"/>, and writes them back.</summary>
    public void Update(Action<Settings> mutate)
    {
        var settings = Read();
        mutate(settings);
        Write(settings);
    }
}
