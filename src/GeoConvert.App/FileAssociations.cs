namespace GeoConvert.App;

/// <summary>
/// Registers this app as the handler for the supported map file extensions, per-user (no admin needed)
/// under <c>HKCU\Software\Classes</c>. One ProgId is created and pointed at the running executable, and
/// every readable map extension is bound to it (set as the default and added to its OpenWith list). The
/// binding is fully reversible via <see cref="Unassociate"/>.
/// </summary>
public static class FileAssociations
{
    const string ProgId = "GeoConvert.Map";
    const string ProgIdLabel = "GeoConvert Map";

    const int ShcneAssocchanged = 0x08000000;
    const uint ShcnfIdlist = 0;

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>The extensions bound to the app — every format that can be read into the editor.</summary>
    public static IReadOnlyList<string> Extensions => ConversionService.ReadableExtensions;

    static string ExecutablePath =>
        Environment.ProcessPath ??
        throw new InvalidOperationException("Could not resolve the running executable path.");

    /// <summary>True when the ProgId is registered and bound to the first supported extension.</summary>
    public static bool IsAssociated()
    {
        using var classes = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extensions[0]}");
        return classes?.GetValue(null) as string == ProgId;
    }

    /// <summary>Binds every supported map extension to this app.</summary>
    public static void Associate()
    {
        var executable = ExecutablePath;

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, ProgIdLabel);
            using (var icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{executable}\",0");
            }

            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{executable}\" \"%1\"");
        }

        foreach (var extension in Extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
            // Set as the default handler (the "bind" the user asked for) and also advertise the ProgId in
            // the extension's OpenWith list so the app shows up there and the binding is cleanly removable.
            key.SetValue(null, ProgId);
            using var openWith = key.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    /// <summary>Removes the bindings created by <see cref="Associate"/>, leaving other handlers intact.</summary>
    public static void Unassociate()
    {
        foreach (var extension in Extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: true);
            if (key == null)
            {
                continue;
            }

            // Only clear the default if it still points at us — never stomp a handler the user has since
            // chosen.
            if (key.GetValue(null) as string == ProgId)
            {
                // "" is the name of a key's default value (DeleteValue, unlike GetValue, won't take null).
                key.DeleteValue(string.Empty, throwOnMissingValue: false);
            }

            using var openWith = key.OpenSubKey("OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        NotifyShell();
    }

    static void NotifyShell() =>
        // Tell Explorer the associations changed so icons / "Open with" refresh without a sign-out.
        SHChangeNotify(ShcneAssocchanged, ShcnfIdlist, IntPtr.Zero, IntPtr.Zero);
}
