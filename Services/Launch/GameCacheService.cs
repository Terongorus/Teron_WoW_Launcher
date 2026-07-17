using System;
using System.IO;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Launch;

/// <summary>
/// Clears the client's WDB cache folder (cached item/creature/quest/NPC data) on request — the
/// client rebuilds it automatically from the server on next launch, so deleting it is always safe.
/// Stale WDB entries are a common source of display bugs on private servers whose data doesn't
/// match retail (wrong item names/tooltips/icons), since the client trusts its own cache over
/// what the server sends unless the cache is cleared.
/// </summary>
public sealed class GameCacheService
{
    private const string WdbFolderName = "WDB";

    private readonly Logger _log = Logger.Instance;

    public string WdbDir(string wowDir) => Path.Combine(wowDir, WdbFolderName);

    /// <summary>Deletes the WDB folder if present. Best-effort: a locked file in there shouldn't
    /// block the launch that triggered this.</summary>
    public void Clear(string wowDir)
    {
        string dir = WdbDir(wowDir);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            DirectoryHelper.DeleteRecursive(dir);
            _log.Info("Cleared WDB client cache before launch.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not clear WDB client cache: {ex.Message}");
        }
    }
}
