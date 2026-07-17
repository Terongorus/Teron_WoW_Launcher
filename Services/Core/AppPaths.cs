using System;
using System.IO;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// Central location for the launcher's own GLOBAL data (settings shared across every WoW
/// directory it manages). Everything here lives under %LocalAppData%\TeronWoWLauncher\ (Local,
/// never Roaming, never a path relative to the executable - the launcher is a standalone app,
/// typically installed to Program Files, and isn't assumed to live inside any WoW folder). Data
/// specific to one particular WoW installation lives inside that installation's own folder instead
/// - see DirectorySettingsService/AddonLibrary/DllListService.
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
