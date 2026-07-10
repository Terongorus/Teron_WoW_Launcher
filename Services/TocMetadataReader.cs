using System;
using System.IO;
using System.Linq;

namespace TeronWoWLauncher.Services;

/// <summary>Reads the "## Title:" and "## Version:" fields out of an addon folder's .toc file.</summary>
public static class TocMetadataReader
{
    /// <summary>
    /// Best-effort only, like <see cref="DllMetadataReader"/>'s equivalent read: a locked file,
    /// transient permissions error, or the folder disappearing mid-scan (e.g. a race with a Refresh)
    /// degrades to "no metadata found" rather than throwing — this runs during MainWindow's own
    /// constructor (via RefreshMetadataFromDisk) and several other UI paths with no enclosing
    /// try/catch, so an unhandled exception here would previously have crashed the whole app.
    /// </summary>
    public static (string? Title, string? Version) Read(string folderPath)
    {
        try
        {
            string? tocPath = Directory.Exists(folderPath)
                ? Directory.GetFiles(folderPath, "*.toc").FirstOrDefault()
                : null;

            if (tocPath is null)
            {
                return (null, null);
            }

            string? title = null;
            string? version = null;

            foreach (string raw in File.ReadLines(tocPath))
            {
                string line = raw.Trim();
                if (TryReadField(line, "## Title:", out string? t))
                {
                    title = t;
                }
                else if (TryReadField(line, "## Version:", out string? v))
                {
                    version = v;
                }
            }

            return (title, version);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool TryReadField(string line, string prefix, out string? value)
    {
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[prefix.Length..].Trim();
            return true;
        }

        value = null;
        return false;
    }
}
