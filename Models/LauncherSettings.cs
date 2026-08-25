namespace TeronWoWLauncher.Models;

/// <summary>
/// Persisted GLOBAL launcher settings (shared across every WoW directory), serialized to
/// %LocalAppData%\TeronWoWLauncher\settings.json. Everything specific to one WoW installation
/// (account/password/realmlist/tweaks/etc.) lives in <see cref="DirectorySettings"/> instead, inside
/// that installation's own folder - see DirectorySettingsService.
/// </summary>
public sealed class LauncherSettings
{
    /// <summary>WoW game directory (folder containing WoW.exe). Null/empty = none configured yet.</summary>
    public string? WowDirectory { get; set; }

    /// <summary>Minimize the launcher window as soon as Play successfully starts the game.</summary>
    public bool MinimizeOnLaunch { get; set; }

    /// <summary>
    /// Realmlist values the user has previously used, most-recent first — offered as a dropdown next
    /// to the free-text realmlist field so switching between a private server's several login realms
    /// (e.g. login2/login3 during downtime) doesn't require retyping the address each time. Global
    /// rather than per-directory - useful regardless of which installation is currently selected.
    /// </summary>
    public List<string> RealmlistHistory { get; set; } = new();

    /// <summary>
    /// Every WoW directory this launcher has been pointed at and confirmed to exist, most-recently-
    /// selected first — powers a quick-switch dropdown on the Home tab so managing several installs
    /// doesn't require re-browsing to a folder every time. Global rather than per-directory, since
    /// the whole point is switching between directories, each of which already tracks its own
    /// addons/tweaks/credentials via DirectorySettings. Pruned of any entry that no longer exists on
    /// disk each time it's refreshed.
    /// </summary>
    public List<string> ManagedDirectories { get; set; } = new();

    /// <summary>Launcher version last shown via the "what's new" popup. Null on first run — no
    /// popup then, since there's nothing to compare against.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>
    /// The global, shared table of known WoW servers/clients - a directory picks one by id
    /// (<see cref="DirectorySettings.ClientProfileId"/>) instead of storing its own client type/URL.
    /// Seeded once, on first run, with a few known-good defaults (see
    /// MainWindow.SeedDefaultClientProfilesIfNeeded) if this list is still empty; the user can freely
    /// add, edit, or delete rows afterward, including the seeded ones.
    /// </summary>
    public List<ClientProfile> ClientProfiles { get; set; } = new();

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
