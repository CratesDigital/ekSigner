using Microsoft.Win32;

namespace EtaSignAgent;

/// <summary>
/// Whether the agent starts when the operator signs in to Windows.
///
/// This lives in the registry rather than as a shortcut in the Startup folder,
/// because the operator has to be able to turn it off from the tray menu and a
/// registry value can be written without COM shortcut interop.
///
/// It also has to be the ONLY autostart mechanism. The installer used to drop a
/// Startup shortcut as well, which meant the agent was always already running
/// after a login — so every manual launch became a second instance and greeted
/// the operator with an error dialog.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ekSigner";

    /// <summary>
    /// What the value was called while the agent was named for one product.
    /// Removed whenever the new one is written: left behind, Windows starts the
    /// old executable too, and the second instance greets the operator with an
    /// error dialog.
    /// </summary>
    private const string LegacyValueName = "ekPOS Signing Agent";

    /// <summary>
    /// The running executable. Under single-file publish, Environment.ProcessPath
    /// is the .exe the operator launched; Assembly.Location would be empty.
    /// </summary>
    private static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return (key?.GetValue(ValueName) ?? key?.GetValue(LegacyValueName)) is string value
                && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns whether the change stuck, so the caller can re-read rather than assume.</summary>
    public static bool Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                var path = ExecutablePath;
                if (string.IsNullOrEmpty(path)) return false;
                // Quoted: the install path contains spaces, and an unquoted
                // value would have Windows try to run "C:\Users\...\ekSigner".
                key.SetValue(ValueName, $"\"{path}\"");
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[autostart] could not update the Run key: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Bring the registry into line with the operator's stored preference.
    ///
    /// Called on every start, because the executable path changes when the agent
    /// is reinstalled to a different directory and a stale path silently stops
    /// it from starting at login.
    /// </summary>
    public static void Apply(AgentConfig config)
    {
        if (config.RunAtLogin)
        {
            Set(true);
        }
        else if (IsEnabled())
        {
            Set(false);
        }
    }
}
