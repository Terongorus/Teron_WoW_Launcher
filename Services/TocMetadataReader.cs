using System;
using System.IO;
using System.Linq;

namespace TeronWoWLauncher.Services;

/// <summary>Reads the "## Title:" and "## Version:" fields out of an addon folder's .toc file.</summary>
public static class TocMetadataReader
{
    public static (string? Title, string? Version) Read(string folderPath)
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
