using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Loads and saves <see cref="LauncherSettings"/> as JSON under %LocalAppData%\TeronWoWLauncher.
/// The password is protected with DPAPI (Windows Data Protection API) scoped to the current
/// user, so the on-disk settings file never contains a recoverable clear-text password.
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
    /// The WoW directory to use: the explicit setting when it points at a real folder, otherwise
    /// the launcher's own directory (the launcher is designed to live inside the WoW folder).
    /// </summary>
    public string ResolveWowDirectory()
    {
        string? dir = Current.WowDirectory;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            return dir;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    public string ResolveWowExePath() => Path.Combine(ResolveWowDirectory(), "WoW.exe");

    // --- Password: DPAPI-protected, current-user scoped. Never persisted in clear text. ---

    public void SetPassword(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || !Current.SavePassword)
        {
            Current.EncryptedPassword = null;
            return;
        }

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            Current.EncryptedPassword = Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to encrypt password: {ex.Message}");
            Current.EncryptedPassword = null;
        }
    }

    public string GetPassword()
    {
        string? encoded = Current.EncryptedPassword;
        if (string.IsNullOrEmpty(encoded))
        {
            return string.Empty;
        }

        try
        {
            byte[] encrypted = Convert.FromBase64String(encoded);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to decrypt saved password: {ex.Message}");
            return string.Empty;
        }
    }
}
