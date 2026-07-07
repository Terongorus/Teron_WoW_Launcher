using System.Collections.Generic;
using System.IO;
using System.Linq;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Finds AddOns folders on disk that aren't tracked yet — addons installed by hand (or from before
/// the addon manager existed) — so they can be offered for adoption instead of being invisible to
/// the manager.
/// </summary>
public sealed class LocalAddonScanner
{
    /// <summary>Every top-level AddOns folder containing a .toc, excluding already-tracked folders.</summary>
    public List<LocalAddonCandidate> Scan(string wowDir, IEnumerable<string> trackedFolders)
    {
        var tracked = new HashSet<string>(trackedFolders, System.StringComparer.OrdinalIgnoreCase);
        var result = new List<LocalAddonCandidate>();

        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        if (!Directory.Exists(addonsDir))
        {
            return result;
        }

        foreach (string dir in Directory.EnumerateDirectories(addonsDir))
        {
            string folderName = Path.GetFileName(dir);
            if (tracked.Contains(folderName))
            {
                continue;
            }

            bool hasToc = Directory.GetFiles(dir, "*.toc").Length > 0;
            if (!hasToc)
            {
                continue; // not an addon folder
            }

            (string? title, string? version) = TocMetadataReader.Read(dir);
            result.Add(new LocalAddonCandidate { FolderName = folderName, Title = title, Version = version });
        }

        return result.OrderBy(c => c.FolderName, System.StringComparer.OrdinalIgnoreCase).ToList();
    }
}
