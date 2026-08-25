using System;
using System.IO;
using System.Text.Json;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// Loads and saves the GLOBAL <see cref="LauncherSettings"/> as JSON under
/// %LocalAppData%\TeronWoWLauncher. Per-installation data (including the DPAPI-protected password)
/// lives in <see cref="DirectorySettingsService"/> instead - see that class's own doc comment.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _log = Logger.Instance;

    public LauncherSettings Current { get; private set; } = new();

    public LauncherSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFilePath))
            {
                string json = File.ReadAllText(AppPaths.SettingsFilePath);
                LauncherSettings? loaded = JsonSerializer.Deserialize<LauncherSettings>(json);
                if (loaded is not null)
                {
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load settings; using defaults. {ex.Message}");
            Current = new LauncherSettings();
        }

        return Current;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataRoot();
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            AtomicFile.WriteAllText(AppPaths.SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// The configured WoW directory, or "" if none is set (or the saved one no longer exists) -
    /// deliberately not a fallback to the launcher's own location: the launcher is a standalone app
    /// (installed to Program Files by the installer) that can manage any number of WoW directories,
    /// none of which have anything to do with where the launcher itself happens to run from. An
    /// empty result means "not configured yet" - callers should treat that as a real state (Play
    /// shows its Install prompt, detection features show nothing) rather than guessing at a folder.
    /// </summary>
    public string ResolveWowDirectory()
    {
        string? dir = Current.WowDirectory;
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : string.Empty;
    }

    public string ResolveWowExePath() => Path.Combine(ResolveWowDirectory(), "WoW.exe");
}
