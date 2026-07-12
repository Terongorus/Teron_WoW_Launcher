namespace TeronWoWLauncher.Models;

/// <summary>
/// Persisted launcher settings. Serialized to %LocalAppData%\TeronWoWLauncher\settings.json.
/// The password is never stored here in clear text — only <see cref="EncryptedPassword"/>
/// (DPAPI ciphertext) is persisted; see <c>SettingsService</c>.
/// </summary>
public sealed class LauncherSettings
{
    /// <summary>WoW game directory (folder containing WoW.exe). Null = use the launcher's folder.</summary>
    public string? WowDirectory { get; set; }

    /// <summary>URL of the vanilla client ZIP to download. Null = use the built-in default.</summary>
    public string? ClientDownloadUrl { get; set; }

    /// <summary>Remote client archive signature recorded at the time of the last successful install/update.</summary>
    public string? InstalledClientSignature { get; set; }

    /// <summary>
    /// DLL file names the user has explicitly dismissed from the DLLs tab's "detected" scan (e.g.
    /// files that aren't actually meant for injection). Never offered again until un-ignored in Settings.
    /// </summary>
    public List<string> IgnoredDetectedDlls { get; set; } = new();

    public string Account { get; set; } = string.Empty;

    /// <summary>DPAPI-protected password (base64), scoped to the current Windows user.</summary>
    public string? EncryptedPassword { get; set; }

    public bool SavePassword { get; set; } = true;

    public bool AutoLoginEnabled { get; set; }

    /// <summary>Delay (ms) after the game window appears before typing credentials.</summary>
    public int LoginDelayMs { get; set; } = 4000;

    /// <summary>Delete the WDB client cache folder before every launch, so stale cached item/quest/
    /// NPC data (a common source of display bugs on private servers) can't linger.</summary>
    public bool CleanWdbBeforeLaunch { get; set; }

    /// <summary>Minimize the launcher window as soon as Play successfully starts the game.</summary>
    public bool MinimizeOnLaunch { get; set; }

    /// <summary>Desired realmlist value written to realmlist.wtf (e.g. "set realmlist logon.server").</summary>
    public string Realmlist { get; set; } = string.Empty;

    /// <summary>
    /// Realmlist values the user has previously used, most-recent first — offered as a dropdown next
    /// to the free-text realmlist field so switching between a private server's several login realms
    /// (e.g. login2/login3 during downtime) doesn't require retyping the address each time.
    /// </summary>
    public List<string> RealmlistHistory { get; set; } = new();

    /// <summary>Ids of executable patches the user has enabled (applied in fixed catalog order).</summary>
    public List<string> EnabledPatchIds { get; set; } = new();

    /// <summary>Chosen values for parameterized patches, keyed by patch id.</summary>
    public Dictionary<string, double> PatchParameters { get; set; } = new();

    /// <summary>
    /// Signature of the patch selection last written to WoW.exe. When it matches the current
    /// selection, the launcher skips re-patching entirely instead of rebuilding every launch.
    /// </summary>
    public string? AppliedPatchSignature { get; set; }

    /// <summary>
    /// SHA-256 of WoW.exe.backup at the moment it was created, so a later mismatch (disk corruption,
    /// an interrupted write from before the atomic-rename fix, or external tampering) can be detected
    /// instead of silently rebuilding every future patch from a backup that's no longer truly pristine.
    /// </summary>
    public string? PristineBackupHash { get; set; }

    // --- Window placement, restored on the next launch. Null = use the XAML defaults (first run,
    // or the saved position no longer falls on any connected monitor). Named to avoid colliding with
    // System.Windows.Window's own WindowState/WindowStyle properties in the code that reads these. ---

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>"Normal", "Maximized", or "Minimized" — matches <see cref="System.Windows.WindowState"/>.</summary>
    public string? SavedWindowState { get; set; }
}
