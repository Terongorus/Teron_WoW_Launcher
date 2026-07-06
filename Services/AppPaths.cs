using System;
using System.IO;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Central location for all launcher-owned data. Everything lives under
/// %LocalAppData%\TeronWoWLauncher\ (Local, never Roaming, never a path relative to the
/// executable). The launcher sits inside the WoW game folder, which may be read-only for a
/// non-admin user and is shared across Windows accounts, so it is never used for our own data.
/// </summary>
public static class AppPaths
{
    /// <summary>%LocalAppData%\TeronWoWLauncher</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeronWoWLauncher");

    /// <summary>%LocalAppData%\TeronWoWLauncher\Logs</summary>
    public static string LogsDirectory { get; } = Path.Combine(DataRoot, "Logs");

    /// <summary>%LocalAppData%\TeronWoWLauncher\error.log</summary>
    public static string ErrorLogPath { get; } = Path.Combine(DataRoot, "error.log");

    /// <summary>%LocalAppData%\TeronWoWLauncher\settings.json</summary>
    public static string SettingsFilePath { get; } = Path.Combine(DataRoot, "settings.json");

    /// <summary>Ensure the data root exists. Idempotent; safe to call before every write.</summary>
    public static void EnsureDataRoot() => Directory.CreateDirectory(DataRoot);
}
