using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// Loads and saves <see cref="DirectorySettings"/> as JSON inside a specific WoW directory
/// (&lt;wowDir&gt;\.teronwow-directory-settings.json) — mirrors <see cref="SettingsService"/>'s own
/// shape, but scoped per installation instead of one shared global file, matching the same
/// per-directory pattern already used for addon tracking (<c>AddonLibrary</c>) and DLL tracking
/// (<c>DllListService</c>'s dlls.txt). The password is DPAPI-protected the same way
/// <see cref="SettingsService"/> used to; only <see cref="DirectorySettings.EncryptedPassword"/>
/// (ciphertext) is ever persisted.
/// </summary>
public sealed class DirectorySettingsService
{
    private const string FileName = ".teronwow-directory-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Every field that used to live in the single global settings.json before per-directory scoping -
    // stripped from that file the moment its data is migrated into the FIRST directory that ever asks
    // for it (see StripLegacyFieldsFromGlobalSettings), so a second/third/Nth new directory doesn't
    // just keep re-adopting the same stale snapshot forever.
    private static readonly string[] LegacyPerDirectoryKeys =
    {
        "InstalledClientSignature", "Account", "EncryptedPassword", "SavePassword", "AutoLoginEnabled",
        "LoginDelayMs", "Realmlist", "EnabledPatchIds", "PatchParameters", "AppliedPatchSignature",
        "PristineBackupHash", "CleanWdbBeforeLaunch", "IgnoredDetectedDlls",
    };

    private readonly Logger _log = Logger.Instance;

    public DirectorySettings Current { get; private set; } = new();

    public static string FilePath(string wowDir) => Path.Combine(wowDir, FileName);

    public DirectorySettings Load(string wowDir)
    {
        string path = FilePath(wowDir);
        try
        {
            MigrateLegacyGlobalFieldsIfPresent(path);

            if (File.Exists(path))
            {
                DirectorySettings? loaded = JsonSerializer.Deserialize<DirectorySettings>(File.ReadAllText(path));
                Current = loaded ?? new DirectorySettings();
            }
            else
            {
                Current = new DirectorySettings();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load directory settings; using defaults. {ex.Message}");
            Current = new DirectorySettings();
        }

        return Current;
    }

    public void Save(string wowDir)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath(wowDir), JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save directory settings: {ex.Message}");
        }
    }

    // One-time migration: every one of these fields used to live in the single global settings.json
    // (before per-directory scoping) - which is exactly the bug this fixes, since two directories
    // sharing one account/realmlist/tweak-selection made no sense once the launcher started managing
    // more than one WoW install. The first directory Load() is called for after upgrading adopts
    // whatever that old global file had as its own starting point; any OTHER directory (which has no
    // reason to inherit settings that belonged to a different install) just starts empty, since by
    // then LauncherSettings.cs no longer even declares these properties for normal deserialization to
    // find. Reads the raw JSON directly (JsonDocument) rather than through LauncherSettings, since
    // that type has already dropped these fields.
    private void MigrateLegacyGlobalFieldsIfPresent(string targetPath)
    {
        if (File.Exists(targetPath) || !File.Exists(AppPaths.SettingsFilePath))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFilePath));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("Account", out _))
            {
                return; // already-migrated or never had these fields to begin with
            }

            var migrated = new DirectorySettings
            {
                InstalledClientSignature = GetString(root, "InstalledClientSignature"),
                Account = GetString(root, "Account") ?? string.Empty,
                EncryptedPassword = GetString(root, "EncryptedPassword"),
                SavePassword = GetBool(root, "SavePassword", true),
                AutoLoginEnabled = GetBool(root, "AutoLoginEnabled", false),
                LoginDelayMs = GetInt(root, "LoginDelayMs", 4000),
                Realmlist = GetString(root, "Realmlist") ?? string.Empty,
                AppliedPatchSignature = GetString(root, "AppliedPatchSignature"),
                PristineBackupHash = GetString(root, "PristineBackupHash"),
                CleanWdbBeforeLaunch = GetBool(root, "CleanWdbBeforeLaunch", false),
            };

            if (root.TryGetProperty("EnabledPatchIds", out JsonElement idsEl) && idsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in idsEl.EnumerateArray())
                {
                    if (id.GetString() is { } s)
                    {
                        migrated.EnabledPatchIds.Add(s);
                    }
                }
            }

            if (root.TryGetProperty("PatchParameters", out JsonElement paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in paramsEl.EnumerateObject())
                {
                    migrated.PatchParameters[prop.Name] = prop.Value.GetDouble();
                }
            }

            if (root.TryGetProperty("IgnoredDetectedDlls", out JsonElement dllsEl) && dllsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement name in dllsEl.EnumerateArray())
                {
                    if (name.GetString() is { } s)
                    {
                        migrated.IgnoredDetectedDlls.Add(s);
                    }
                }
            }

            AtomicFile.WriteAllText(targetPath, JsonSerializer.Serialize(migrated, JsonOptions));
            _log.Info($"Migrated the old shared settings.json's per-installation fields into {targetPath}.");

            // Only the first directory this ever runs for should adopt the old global data - without
            // this, the check above (root.TryGetProperty("Account", ...)) keeps succeeding forever,
            // since nothing else ever rewrites settings.json to drop these now-unused keys, and every
            // later directory silently re-adopts this same snapshot instead of starting empty.
            StripLegacyFieldsFromGlobalSettings();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not migrate old per-installation settings: {ex.Message}");
        }
    }

    private void StripLegacyFieldsFromGlobalSettings()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFilePath)) is JsonObject obj)
            {
                foreach (string key in LegacyPerDirectoryKeys)
                {
                    obj.Remove(key);
                }

                AtomicFile.WriteAllText(AppPaths.SettingsFilePath, obj.ToJsonString(JsonOptions));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not strip legacy per-installation fields from settings.json: {ex.Message}");
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool GetBool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out JsonElement el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : fallback;

    private static int GetInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : fallback;

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
