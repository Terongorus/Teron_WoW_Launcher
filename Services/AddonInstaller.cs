using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Extracts an addon archive and installs each addon it contains into Interface\AddOns.
///
/// A WoW addon is a folder that directly contains a <c>.toc</c> file, and WoW requires the folder to
/// be named after that .toc. We locate every directory holding a .toc, keep only the outermost ones
/// (so a monorepo's sub-addons install individually, while an addon whose own lib folders also have
/// .tocs installs as one unit), and copy each into AddOns under the .toc's name.
/// </summary>
public sealed class AddonInstaller
{
    private readonly Logger _log = Logger.Instance;

    /// <summary>Install every addon in the archive; returns each installed folder with its .toc metadata.</summary>
    public List<InstalledFolderInfo> InstallFromArchive(string archivePath, string wowDir)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"teronwow_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            ExtractArchive(archivePath, temp);

            List<(string Dir, string Name)> addons = FindOutermostTocDirs(temp);
            if (addons.Count == 0)
            {
                throw new InvalidOperationException(
                    "No addon (.toc file) was found in the archive. Only .zip addons are supported.");
            }

            string addonsDir = AddonPaths.EnsureAddOnsDir(wowDir);
            var installed = new List<InstalledFolderInfo>();
            foreach ((string dir, string name) in addons)
            {
                string dest = Path.Combine(addonsDir, name);
                if (Directory.Exists(dest))
                {
                    Directory.Delete(dest, recursive: true);
                }

                CopyDirectory(dir, dest);
                (string? title, string? version) = TocMetadataReader.Read(dest);
                installed.Add(new InstalledFolderInfo(name, title, version));
                _log.Info($"Installed addon: {name}");
            }

            return installed;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    // Extract zip/rar/7z; ArchiveFactory auto-detects the format from the archive's content.
    private static void ExtractArchive(string archivePath, string destDir)
    {
        ArchiveFactory.WriteToDirectory(
            archivePath, destDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
    }

    private static List<(string Dir, string Name)> FindOutermostTocDirs(string root)
    {
        List<string> tocDirs = Directory
            .EnumerateFiles(root, "*.toc", SearchOption.AllDirectories)
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<(string, string)>();
        foreach (string dir in tocDirs)
        {
            // Skip if another .toc directory is an ancestor of this one.
            if (tocDirs.Any(other => !PathEquals(other, dir) && IsAncestor(other, dir)))
            {
                continue;
            }

            var tocNames = Directory.GetFiles(dir, "*.toc")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            string dirName = new DirectoryInfo(dir).Name;
            string name = tocNames.Contains(dirName, StringComparer.OrdinalIgnoreCase)
                ? dirName
                : tocNames.FirstOrDefault() ?? dirName;

            result.Add((dir, SanitizeFolderName(name!)));
        }

        return result;
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(NormalizeDir(a), NormalizeDir(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsAncestor(string ancestor, string descendant)
    {
        string a = NormalizeDir(ancestor) + Path.DirectorySeparatorChar;
        string d = NormalizeDir(descendant) + Path.DirectorySeparatorChar;
        return d.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDir(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string SanitizeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }
}
