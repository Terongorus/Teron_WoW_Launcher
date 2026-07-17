using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TeronWoWLauncher.Models;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Addons;

/// <summary>
/// Installs each addon found in a downloaded/checked-out content directory into Interface\AddOns.
///
/// A WoW addon is a folder that directly contains a <c>.toc</c> file, and WoW requires the folder
/// to be named after that .toc — and it only ever looks one level deep (AddOns\&lt;Name&gt;\&lt;Name&gt;.toc),
/// so every directory that has its own .toc anywhere in the tree becomes its own top-level AddOns
/// entry, regardless of nesting. This matters for "core addon + on-demand-loaded modules" repos
/// (e.g. TeronDebugTools), where the root has its own .toc *and* several subfolders each have their
/// own — every one of them needs to land as an independent AddOns\&lt;Name&gt; folder, or the modules
/// end up nested two levels deep inside the core addon's folder, invisible to the game's loader.
/// When copying each addon's own folder, any subtree that is itself one of the other identified
/// .toc folders is skipped, so a module never also ends up duplicated inside its parent's copy.
/// </summary>
public sealed class AddonInstaller
{
    private readonly Logger _log = Logger.Instance;

    /// <summary>Install every addon found under a content directory; returns each installed folder with its .toc metadata.</summary>
    public List<InstalledFolderInfo> InstallFromDirectory(string contentDir, string wowDir)
    {
        List<(string Dir, string Name)> addons = FindAllTocDirs(contentDir);
        if (addons.Count == 0)
        {
            throw new InvalidOperationException(
                "No addon (.toc file) was found. Only .zip addons and GitHub repos are supported.");
        }

        string addonsDir = AddonPaths.EnsureAddOnsDir(wowDir);
        var installed = new List<InstalledFolderInfo>();
        foreach ((string dir, string name) in addons)
        {
            string dest = Path.Combine(addonsDir, name);
            if (Directory.Exists(dest))
            {
                DirectoryHelper.DeleteRecursive(dest);
            }

            List<string> excludeDirs = addons.Where(a => !PathEquals(a.Dir, dir)).Select(a => a.Dir).ToList();
            CopyDirectory(dir, dest, excludeDirs);
            (string? title, string? version) = TocMetadataReader.Read(dest);
            installed.Add(new InstalledFolderInfo(name, title, version));
            _log.Info($"Installed addon: {name}");
        }

        return installed;
    }

    private static List<(string Dir, string Name)> FindAllTocDirs(string root)
    {
        List<string> tocDirs = Directory
            .EnumerateFiles(root, "*.toc", SearchOption.AllDirectories)
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<(string, string)>();
        foreach (string dir in tocDirs)
        {
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

    // Skips any subtree in excludeDirs (each is itself a separately-installed addon folder), so a
    // nested module never also gets duplicated inside its parent's copy.
    private static void CopyDirectory(string source, string dest, IReadOnlyCollection<string> excludeDirs)
    {
        string destFull = Path.GetFullPath(dest);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar))
        {
            destFull += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsExcluded(dir, excludeDirs))
            {
                continue;
            }

            Directory.CreateDirectory(ResolveDestPath(source, dir, destFull));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string? fileDir = Path.GetDirectoryName(file);
            if (fileDir is not null && IsExcluded(fileDir, excludeDirs))
            {
                continue;
            }

            File.Copy(file, ResolveDestPath(source, file, destFull), overwrite: true);
        }
    }

    /// <summary>
    /// Resolves an entry under source to its counterpart under dest via its relative path, then
    /// verifies the result actually lands inside dest before returning it - a Zip Slip-style guard
    /// (source content ultimately traces back to an addon archive/git checkout, i.e. arbitrary
    /// third-party content, not the launcher's own files). Replaces the previous plain
    /// entryPath.Replace(source, dest), which could behave surprisingly if source ever happened to
    /// appear as a substring elsewhere in a nested path.
    /// </summary>
    private static string ResolveDestPath(string source, string entryPath, string destFullWithSep)
    {
        string relative = Path.GetRelativePath(source, entryPath);
        string resolved = Path.GetFullPath(Path.Combine(destFullWithSep, relative));
        if (!resolved.StartsWith(destFullWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to copy '{entryPath}' — its resolved destination falls outside the install folder.");
        }

        return resolved;
    }

    private static bool IsExcluded(string dir, IReadOnlyCollection<string> excludeDirs)
        => excludeDirs.Any(ex => PathEquals(ex, dir) || IsAncestor(ex, dir));
}
