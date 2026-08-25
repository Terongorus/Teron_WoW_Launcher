using System;
using System.Diagnostics;
using System.IO;

namespace TeronWoWLauncher.Services.Launch;

/// <summary>
/// Detects whether a WoW.exe process from a specific game folder is currently running. Used to avoid
/// rewriting WoW.exe while the OS has it locked as a running image (which fails with an IOException),
/// and to give a clear, actionable message instead of a raw exception.
/// </summary>
public sealed class GameProcessService
{
    /// <summary>True if a running "WoW" process's executable path is inside <paramref name="wowDir"/>.</summary>
    public bool IsRunning(string wowDir)
    {
        // No directory configured yet (a real, reachable state now that the launcher no longer
        // assumes a fallback location) - Path.GetFullPath rejects "" outright, and there's nothing
        // meaningful to match a running process's path against anyway.
        if (string.IsNullOrWhiteSpace(wowDir))
        {
            return false;
        }

        string normalizedDir = NormalizeDir(wowDir);

        foreach (Process process in Process.GetProcessesByName("WoW"))
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) &&
                        string.Equals(NormalizeDir(Path.GetDirectoryName(path) ?? string.Empty), normalizedDir,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Access denied (different user/elevation) or the process exited mid-check — ignore it.
                }
            }
        }

        return false;
    }

    private static string NormalizeDir(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
