namespace GeoConvert.App;

/// <summary>
/// Persisted user preferences, stored as JSON under <see cref="SettingsManager.DefaultSettingsPath"/>.
/// Kept deliberately small — the only thing that has to survive between runs is whether the first-run
/// file-association prompt has already been shown.
/// </summary>
public class Settings
{
    /// <summary>
    /// True once the user has been asked (on first launch) whether to bind the supported map formats to
    /// this app. Gates the one-time prompt so it never reappears, whatever the user answered.
    /// </summary>
    public bool AssociationsPrompted { get; set; }
}
