using System;
using System.IO;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// Resolves a per-directory launcher data file's path inside the shared ".teronwow" subfolder,
/// transparently migrating a legacy copy sitting directly in the WoW directory root (from before
/// this subfolder existed) into it the first time it's resolved. Every per-directory JSON sidecar
/// (directory settings, addons, MPQ/DLL metadata) plus dlls.txt.cache lives here now, purely to keep
/// the game folder from accumulating launcher-internal clutter — nothing the game itself or
/// PatchService's rebuild logic reads is ever routed through this (dlls.txt, realmlist.wtf,
/// WoW.exe/WoW.exe.backup stay exactly where they've always been, untouched by this class).
///
/// Deliberately simpler than the earlier global-to-per-directory migration (see
/// DirectorySettingsService's own migration comments): that one needed a dedicated marker file
/// because multiple directories raced to adopt the SAME shared source. This is a root-to-subfolder
/// move fully contained within one directory — "does the new path already exist" is itself a
/// sufficient, self-contained idempotency check, no marker needed.
/// </summary>
public static class PerDirectoryDataFolder
{
    public const string FolderName = ".teronwow";

    /// <summary>Returns the file's path inside &lt;wowDir&gt;\.teronwow\, migrating a legacy copy
    /// sitting directly in wowDir into that folder first if the new location doesn't already have
    /// one. Safe to call on every Load()/Save() - a no-op once migrated. Never deletes or loses data:
    /// if the move itself fails (locked file, permissions), falls back to the legacy path for this
    /// run so the caller still finds the real data - migration simply gets retried next call.</summary>
    public static string ResolvePath(string wowDir, string fileName)
    {
        string folder = Path.Combine(wowDir, FolderName);
        string newPath = Path.Combine(folder, fileName);

        if (File.Exists(newPath))
        {
            return newPath;
        }

        string legacyPath = Path.Combine(wowDir, fileName);
        if (!File.Exists(legacyPath))
        {
            return newPath;
        }

        try
        {
            Directory.CreateDirectory(folder);
            File.Move(legacyPath, newPath);
            Logger.Instance.Info($"Moved {fileName} into {FolderName}\\.");
            return newPath;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn($"Could not migrate {fileName} into {FolderName}\\ (will retry later): {ex.Message}");
            return legacyPath;
        }
    }
}
