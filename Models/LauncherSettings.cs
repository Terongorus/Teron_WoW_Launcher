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
}
