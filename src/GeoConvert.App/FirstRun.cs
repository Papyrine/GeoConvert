namespace GeoConvert.App;

/// <summary>
/// The one-time, first-launch prompt that offers to bind the supported map formats to this app. Gated by
/// <see cref="Settings.AssociationsPrompted"/> so it appears exactly once, whatever the user answers.
/// </summary>
public static class FirstRun
{
    public static void PromptForAssociationsIfNeeded(SettingsManager settingsManager, IWin32Window owner)
    {
        var settings = settingsManager.Read();
        if (settings.AssociationsPrompted)
        {
            return;
        }

        // Persist first, so a crash in the registry step never re-prompts on every launch.
        settingsManager.Update(_ => _.AssociationsPrompted = true);

        var extensions = string.Join(" ", FileAssociations.Extensions);
        var result = MessageBox.Show(
            owner,
            $"""
            Bind the supported map formats to GeoConvert, so double-clicking one opens it here?

            This sets GeoConvert as the handler for:
              {extensions}

            Note: this includes the shared .json and .csv extensions. The change is per-user (no admin
            needed) and can be undone any time from Tools ▸ Remove file associations.
            """,
            "Associate map files with GeoConvert?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            FileAssociations.Associate();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                owner,
                $"Could not set file associations:\n{exception.Message}",
                "GeoConvert",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
