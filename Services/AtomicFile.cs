using System;
using System.IO;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Writes file content via a same-directory temp file plus an atomic rename, so a crash, power loss,
/// or a transient antivirus lock mid-write can never leave the destination truncated — it's always
/// either the complete old file or the complete new one, never a half-written one (a plain
/// File.Copy/WriteAllBytes/WriteAllText straight onto the real path has no such guarantee). Shared by
/// PatchService (WoW.exe and its backup), SettingsService (settings.json), and AddonLibrary
/// (addons.json) — anywhere a corrupted write means losing real user state, not just a transient
/// glitch.
///
/// Also translates the two distinct failure modes .NET can throw here into an actionable message: an
/// IOException usually means the destination is locked by another process (Win32 error 32).
/// UnauthorizedAccessException means something entirely different — folder permissions, an elevation
/// requirement, or antivirus blocking the write (Win32 error 5) — so it gets its own correct
/// diagnosis instead of being misattributed to "something has it open".
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string dest, byte[] content) => Write(dest, temp => File.WriteAllBytes(temp, content));

    public static void WriteAllText(string dest, string content) => Write(dest, temp => File.WriteAllText(temp, content));

    private static void Write(string dest, Action<string> writeTemp)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(dest))!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $"{Path.GetFileName(dest)}.{Guid.NewGuid():N}.tmp");
        try
        {
            writeTemp(temp);

            try
            {
                File.Move(temp, dest, overwrite: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Could not write '{Path.GetFileName(dest)}' — access denied. This usually means the " +
                    "containing folder needs administrator rights, has restrictive permissions, or " +
                    "antivirus software is blocking the write.", ex);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Could not write '{Path.GetFileName(dest)}' — it's currently in use by another process.", ex);
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup; already moved on success */ }
        }
    }
}
