using System;
using System.IO;

namespace TeronWoWLauncher.Services;

/// <summary>Locations for addon management within a WoW install.</summary>
public static class AddonPaths
{
    /// <summary>&lt;wowDir&gt;\Interface\AddOns</summary>
    public static string AddOnsDir(string wowDir) => Path.Combine(wowDir, "Interface", "AddOns");

    public static string EnsureAddOnsDir(string wowDir)
    {
        string dir = AddOnsDir(wowDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>%LocalAppData%\TeronWoWLauncher\addons.json</summary>
    public static string AddonsFilePath => Path.Combine(AppPaths.DataRoot, "addons.json");

    /// <summary>%LocalAppData%\TeronWoWLauncher\AddonRepos</summary>
    public static string AddonRepoCacheRoot => Path.Combine(AppPaths.DataRoot, "AddonRepos");

    /// <summary>
    /// Persistent local git clone location for a GitHub-tracked addon, one per owner/repo — kept
    /// across app runs so update checks/pulls are incremental instead of a fresh clone every time.
    /// </summary>
    public static string AddonRepoCacheDir(string owner, string repo)
    {
        string folder = $"{owner}__{repo}";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            folder = folder.Replace(c, '_');
        }

        return Path.Combine(AddonRepoCacheRoot, folder.ToLowerInvariant());
    }
}
