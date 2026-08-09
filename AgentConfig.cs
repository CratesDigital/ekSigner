using System.Text.Json;

namespace EtaSignAgent;

/// <summary>
/// Agent settings, persisted next to the executable.
///
/// The paired origins are the security boundary: an agent that signs whatever
/// it is asked, from wherever, is a forgery service. Nothing is signed for an
/// origin the operator has not explicitly paired.
/// </summary>
public sealed class AgentConfig
{
    public int Port { get; set; } = 8420;

    /// <summary>Minutes a PIN stays valid before it must be entered again.</summary>
    public int UnlockMinutes { get; set; } = 60;

    /// <summary>
    /// Start with Windows. On by default — an agent that has to be launched by
    /// hand every morning is one that will be missing when someone tries to
    /// sign. Kept here rather than read back from the registry so an operator
    /// who deliberately turns it off stays off across upgrades.
    /// </summary>
    public bool RunAtLogin { get; set; } = true;

    /// <summary>ekPOS origins allowed to request signatures, e.g. https://ekpos.withcrates.com</summary>
    public List<string> PairedOrigins { get; set; } = new();

    /// <summary>Shown in the agent's own window; the operator types it into ekPOS once.</summary>
    public string PairingCode { get; set; } = "";

    /// <summary>Extra PKCS#11 module paths, for a driver installed somewhere unusual.</summary>
    public List<string> ExtraModules { get; set; } = new();

    /// <summary>
    /// The roaming profile, not the install directory. An installer overwrites
    /// its own directory on upgrade and deletes it on uninstall, so config kept
    /// beside the executable would cost the operator their pairing and mint a
    /// new pairing code every time the agent updated.
    /// </summary>
    private static string ConfigDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ekPOS");

    private static string Path => System.IO.Path.Combine(ConfigDir, "agent-config.json");

    /// <summary>Where builds that predate the installer kept it — beside the exe.</summary>
    private static string LegacyPath => System.IO.Path.Combine(AppContext.BaseDirectory, "agent-config.json");

    public static AgentConfig Load()
    {
        // Carry over a config from before the installer, so an operator who has
        // already paired and chosen a certificate does not have to do it again.
        if (!File.Exists(Path) && File.Exists(LegacyPath))
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.Move(LegacyPath, Path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] could not carry over {LegacyPath}: {ex.Message}");
            }
        }

        if (File.Exists(Path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(Path));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.PairingCode))
                    {
                        loaded.PairingCode = NewPairingCode();
                        loaded.Save();
                    }
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] {Path} is unreadable, starting fresh: {ex.Message}");
            }
        }

        var fresh = new AgentConfig { PairingCode = NewPairingCode() };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsPaired(string? origin)
        => !string.IsNullOrEmpty(origin)
           && PairedOrigins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));

    public void Pair(string origin)
    {
        if (!IsPaired(origin))
        {
            PairedOrigins.Add(origin);
            Save();
        }
    }

    /// <summary>
    /// Six digits, from a cryptographic RNG. Short enough to read off a screen
    /// and type, and it only has to survive the pairing window.
    /// </summary>
    private static string NewPairingCode()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 999_999).ToString();

    /// <summary>
    /// Where the vendor PKCS#11 modules live. Every path is probed and all that
    /// exist are loaded, because a workstation may hold either token — or, at a
    /// service bureau, both.
    ///
    /// Verified present on Windows: eps2003csp11.dll and SignatureP11.dll, both
    /// in System32. The rest are documented locations kept as fallbacks, since
    /// vendors move these between driver releases.
    /// </summary>
    public IEnumerable<string> ModulePaths()
    {
        var candidates = new List<string>(ExtraModules);

        if (OperatingSystem.IsWindows())
        {
            var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            candidates.AddRange(new[]
            {
                System.IO.Path.Combine(sys32, "eps2003csp11.dll"),      // Feitian ePass2003
                System.IO.Path.Combine(sys32, "eps2003csp1164.dll"),
                System.IO.Path.Combine(sys32, "SignatureP11.dll"),      // WatchData PROXKey
                System.IO.Path.Combine(sys32, "wdpkcs.dll"),
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange(new[]
            {
                "/usr/local/lib/libcastle.1.0.0.dylib",                 // Feitian ePass2003
                "/usr/local/lib/libcastle_v2.1.0.0.dylib",
                "/usr/local/lib/wdProxKeyUsbKeyTool/libwdpkcs_Proxkey.dylib",   // PROXKey
            });
        }

        return candidates;
    }
}
