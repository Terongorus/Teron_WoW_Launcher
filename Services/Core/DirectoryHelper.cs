using System;
using System.IO;
using System.Threading;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// A handful of directories this app deletes (addon folders adopted from a local install) can
/// contain a real ".git" checkout — git marks its own pack files (".git/objects/pack/*.idx"
/// and "*.pack") read-only by design, which makes a plain Directory.Delete(path, recursive: true)
/// throw UnauthorizedAccessException instead of actually deleting anything.
/// Additionally, Windows may temporarily deny access to a directory that another process (e.g.
/// the game, Explorer, or an antivirus) holds open — a short retry loop handles those transient
/// locks without surfacing a spurious error to the user.
/// </summary>
public static class DirectoryHelper
{
    private const int RetryCount = 5;
    private const int RetryDelayMs = 200;

    /// <summary>
    /// Deletes a directory tree, clearing read-only attributes first so a nested .git folder
    /// can't block it, and retrying on transient access-denied / file-locked errors.
    /// </summary>
    public static void DeleteRecursive(string path)
    {
        ClearReadOnlyAttributes(path);

        for (int attempt = 1; attempt <= RetryCount; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < RetryCount
                && ex is IOException or UnauthorizedAccessException)
            {
                // Re-clear in case the OS re-applied read-only during a partial delete.
                try { ClearReadOnlyAttributes(path); } catch { /* best-effort */ }
                Thread.Sleep(RetryDelayMs * attempt);
            }
        }

        // Final attempt — let any exception propagate naturally.
        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        var dir = new DirectoryInfo(path);

        // Clear the root directory's own ReadOnly bit as well, not just its children.
        if ((dir.Attributes & FileAttributes.ReadOnly) != 0)
        {
            dir.Attributes &= ~FileAttributes.ReadOnly;
        }

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
