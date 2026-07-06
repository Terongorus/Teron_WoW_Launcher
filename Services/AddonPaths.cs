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
}
