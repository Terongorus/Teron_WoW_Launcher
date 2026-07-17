using System;
using System.IO;
using System.Text;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// Appends unhandled exceptions to %LocalAppData%\TeronWoWLauncher\error.log. Must never throw —
/// a failure while logging a crash cannot be allowed to mask the crash itself.
/// </summary>
public static class CrashLog
{
    private static readonly object _gate = new();

    public static void Write(Exception ex)
    {
        try
        {
            AppPaths.EnsureDataRoot();
            var block = new StringBuilder()
                .AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====")
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();

            lock (_gate)
            {
                File.AppendAllText(AppPaths.ErrorLogPath, block, Encoding.UTF8);
            }
        }
        catch { /* logging must never throw */ }

        // Best-effort mirror to the main log so it also shows in the UI when possible.
        try { Logger.Instance.Error("Unhandled exception", ex); } catch { }
    }
}
