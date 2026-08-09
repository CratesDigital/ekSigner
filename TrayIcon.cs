using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace EtaSignAgent;

/// <summary>
/// The tray icon — the only visible sign that a windowless agent is running.
///
/// Without it the agent is indistinguishable from one that failed to start, and
/// the only way to stop it was Task Manager. Both of those are the sort of thing
/// that makes an operator distrust software they were told to install.
///
/// Runs its own message loop on an STA thread. Top-level statements in
/// Program.cs cannot carry [STAThread], and the shell interop behind a context
/// menu wants an STA apartment.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AgentConfig _config;
    private readonly TokenService _tokens;
    private readonly Action _quit;

    private NotifyIcon? _icon;
    private ToolStripMenuItem? _startupItem;
    private System.Windows.Forms.Timer? _refresh;
    private Thread? _thread;
    private bool _settingCheck;

    public TrayIcon(AgentConfig config, TokenService tokens, Action quit)
    {
        _config = config;
        _tokens = tokens;
        _quit = quit;
    }

    /// <summary>Starts the message loop and blocks until the operator quits.</summary>
    public void Run()
    {
        _thread = new Thread(Loop) { IsBackground = false };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _thread.Join();
    }

    private void Loop()
    {
        Application.EnableVisualStyles();

        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("Open agent page", null, (_, _) => Open("/")));
        menu.Items.Add(new ToolStripMenuItem("Unlock token", null, (_, _) => Open("/unlock")));
        menu.Items.Add(new ToolStripSeparator());

        _startupItem = new ToolStripMenuItem("Run when Windows starts")
        {
            CheckOnClick = true,
            Checked = _config.RunAtLogin,
        };
        _startupItem.CheckedChanged += (_, _) => ToggleStartup();
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            _quit();
            Application.ExitThread();
        }));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
            Text = "ekPOS Signing Agent",
        };
        _icon.DoubleClick += (_, _) => Open("/");

        UpdateTooltip();
        _refresh = new System.Windows.Forms.Timer { Interval = 5000 };
        _refresh.Tick += (_, _) => UpdateTooltip();
        _refresh.Start();

        Application.Run();

        _icon.Visible = false;
    }

    /// <summary>
    /// The icon compiled into this executable, so there is one copy of the image
    /// rather than a loose .ico to keep in step with it. ExtractAssociatedIcon
    /// throws on a path it cannot read rather than returning null, and a missing
    /// tray icon is not worth failing to start over.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] could not read the application icon: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    private void ToggleStartup()
    {
        if (_startupItem is null || _settingCheck) return;

        var wanted = _startupItem.Checked;
        if (!Autostart.Set(wanted))
        {
            // Registry write failed. Put the tick back where reality is rather
            // than leaving a menu that lies about what will happen at login —
            // guarded, because assigning Checked raises this handler again.
            _settingCheck = true;
            _startupItem.Checked = Autostart.IsEnabled();
            _settingCheck = false;

            MessageBox.Show(
                "Windows would not let the startup setting be changed.",
                "ekPOS Signing Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.RunAtLogin = wanted;
        _config.Save();
    }

    /// <summary>
    /// Tooltip text is capped at 63 characters by the shell, so this stays terse.
    /// </summary>
    private void UpdateTooltip()
    {
        if (_icon is null) return;

        string state;
        if (_tokens.LoadedModules.Count == 0)
        {
            state = "no token driver found";
        }
        else if (_tokens.IsUnlocked)
        {
            var until = _tokens.UnlockedUntil?.ToLocalTime();
            state = until is null ? "token unlocked" : $"unlocked until {until:HH:mm}";
        }
        else
        {
            state = "token locked";
        }

        _icon.Text = $"ekPOS Signing Agent — {state}";
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_config.Port}{path}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] could not open a browser: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _refresh?.Dispose();
        _icon?.Dispose();
    }
}
