using System;
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
    private readonly Logger _log = Logger.Instance;

    /// <summary>Every top-level AddOns folder containing a .toc, excluding already-tracked folders.
    /// Best-effort: a transient scan failure (locked folder, permissions hiccup) degrades to "none
    /// found" rather than throwing, since this is reachable from an async-void Refresh handler with
    /// no enclosing exception handler.</summary>
    public List<LocalAddonCandidate> Scan(string wowDir, IEnumerable<string> trackedFolders)
    {
        var tracked = new HashSet<string>(trackedFolders, System.StringComparer.OrdinalIgnoreCase);
        var result = new List<LocalAddonCandidate>();

        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        if (!Directory.Exists(addonsDir))
        {
            return result;
        }

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(addonsDir).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not scan {addonsDir} for local addons: {ex.Message}");
            return result;
        }

        foreach (string dir in dirs)
        {
            string folderName = Path.GetFileName(dir);
            if (tracked.Contains(folderName))
            {
                continue;
            }

            bool hasToc;
            try
            {
                hasToc = Directory.GetFiles(dir, "*.toc").Length > 0;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not inspect {dir}: {ex.Message}");
                continue;
            }

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
