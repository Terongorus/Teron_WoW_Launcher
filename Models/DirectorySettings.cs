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
    /// <summary>Remote client archive signature recorded at the time of the last successful install/update.</summary>
    public string? InstalledClientSignature { get; set; }

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
}
