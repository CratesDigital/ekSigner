using System.Runtime.InteropServices;

namespace EtaSignAgent;

/// <summary>
/// A windowless agent that fails at startup fails invisibly: there is no
/// console to print to and no window to show, so the operator sees an agent
/// that simply never appears. Port 8420 already being in use is the realistic
/// case, and "it does nothing at all" is impossible to support over the phone —
/// it is the same dead end the pairing bug produced.
///
/// So a fatal startup error gets a dialog the operator cannot miss, and a line
/// in a log file next to the config for whoever they call.
/// </summary>
internal static class StartupError
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint IconError = 0x10;

    public static void Report(string message)
    {
        Console.Error.WriteLine(message);

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ekPOS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "agent-error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // If even the log cannot be written, the dialog is still worth showing.
        }

        if (OperatingSystem.IsWindows())
        {
            try { MessageBoxW(IntPtr.Zero, message, "ekPOS Signing Agent", IconError); }
            catch { /* nothing left to fall back to */ }
        }
    }
}
