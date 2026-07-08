using System.IO;

namespace TeronWoWLauncher.Services;

/// <summary>
/// A handful of directories this app deletes (addon folders adopted from a local install) can
/// contain a real ".git" checkout — git marks its own pack files (".git/objects/pack/*.idx"
/// and "*.pack") read-only by design, which makes a plain Directory.Delete(path, recursive: true)
/// throw UnauthorizedAccessException instead of actually deleting anything.
/// </summary>
public static class DirectoryHelper
{
    /// <summary>Deletes a directory tree, clearing read-only attributes first so a nested .git folder can't block it.</summary>
    public static void DeleteRecursive(string path)
    {
        ClearReadOnlyAttributes(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        var dir = new DirectoryInfo(path);
        foreach (FileInfo file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        foreach (DirectoryInfo sub in dir.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            if ((sub.Attributes & FileAttributes.ReadOnly) != 0)
            {
                sub.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}
