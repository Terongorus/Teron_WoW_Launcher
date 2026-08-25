using System.Collections.Generic;

namespace TeronWoWLauncher.Models;

/// <summary>
/// Everything the launcher tracks that's actually specific to one WoW installation, persisted to
/// &lt;wowDir&gt;\.teronwow-directory-settings.json — not the global settings.json (see
/// DirectorySettingsService). Two different WoW directories can point at different servers with
/// different accounts/realmlists/tweaks, so none of this can be shared across them.
/// </summary>
public sealed class DirectorySettings
{
    /// <summary>
    /// User-facing label for this profile on Home's Profile list. Optional - when blank, the UI falls
    /// back to showing the directory path itself (or "Default" specifically for the launcher's own
    /// folder, since that raw path is rarely meaningful to the user).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Remote client archive signature recorded at the time of the last successful install/update.</summary>
    public string? InstalledClientSignature { get; set; }

    /// <summary>
    /// Which entry of the global <see cref="LauncherSettings.ClientProfiles"/> table this directory
    /// holds - drives its download URL and which executable tweaks are offered. Null means no profile
    /// has been assigned yet (a fresh directory, or one whose assigned profile was since deleted) -
    /// callers needing the category/URL must handle this explicitly rather than falling back to a
    /// default, since guessing wrong here risks patching the wrong exe layout.
    /// </summary>
    public string? ClientProfileId { get; set; }

    public string Account { get; set; } = string.Empty;

    /// <summary>DPAPI-protected password (base64), scoped to the current Windows user.</summary>
    public string? EncryptedPassword { get; set; }

    public bool SavePassword { get; set; } = true;

    public bool AutoLoginEnabled { get; set; }

    /// <summary>Delay (ms) after the game window appears before typing credentials.</summary>
    public int LoginDelayMs { get; set; } = 4000;

    /// <summary>Desired realmlist value written to realmlist.wtf (e.g. "set realmlist logon.server").</summary>
    public string Realmlist { get; set; } = string.Empty;

    /// <summary>Ids of executable patches the user has enabled (applied in fixed catalog order).</summary>
    public List<string> EnabledPatchIds { get; set; } = new();

    /// <summary>
    /// True once this directory's default-enabled patches (see <see cref="PatchDefinition.DefaultEnabled"/>,
    /// e.g. the TurtleWoW/OctoWoW tweaks that already ship baked into those clients) have been seeded
    /// into <see cref="EnabledPatchIds"/>. Seeding only ever happens once per directory - after that,
    /// an empty <see cref="EnabledPatchIds"/> is trusted as the user's own deliberate "everything off"
    /// choice rather than re-applied over every time the Tweaks tab loads.
    /// </summary>
    public bool PatchDefaultsSeeded { get; set; }

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

    /// <summary>Delete the WDB client cache folder before every launch, so stale cached item/quest/
    /// NPC data (a common source of display bugs on private servers) can't linger.</summary>
    public bool CleanWdbBeforeLaunch { get; set; }

    /// <summary>
    /// DLL file names the user has explicitly dismissed from the DLLs tab's "detected" scan (e.g.
    /// files that aren't actually meant for injection). Never offered again until un-ignored in Settings.
    /// </summary>
    public List<string> IgnoredDetectedDlls { get; set; } = new();

    /// <summary>Forces the WTF/Config.WTF settings some HD/visual MPQ patches require (see
    /// ConfigWtfService.RequiredSettings), re-applied at the start of every Play so it survives the
    /// client's own rewrites of that file between sessions.</summary>
    public bool ConfigWtfRewriteEnabled { get; set; }

    /// <summary>
    /// Each required Config.WTF key's value immediately before the toggle above last forced it (null
    /// = the key didn't exist at all). Captured fresh every time the toggle goes off→on; consumed and
    /// cleared when it goes on→off, so every on/off cycle restores exactly what it changed and nothing
    /// from an earlier cycle.
    /// </summary>
    public Dictionary<string, string?> ConfigWtfOriginalValues { get; set; } = new();
}
