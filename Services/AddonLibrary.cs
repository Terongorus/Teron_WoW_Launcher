using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Sources;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Tracks installed addons (persisted to addons.json) and drives install/remove through the addon
/// sources and installer. Nothing outside addons.json and the game's own Interface\AddOns folder is
/// touched.
/// </summary>
public sealed class AddonLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Logger _log = Logger.Instance;
    private readonly AddonSourceResolver _resolver = new();
    private readonly AddonInstaller _installer = new();

    private List<InstalledAddon> _addons = new();

    public IReadOnlyList<InstalledAddon> Addons => _addons;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeronWoWLauncher");
        return http;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(AddonPaths.AddonsFilePath))
            {
                string json = File.ReadAllText(AddonPaths.AddonsFilePath);
                _addons = JsonSerializer.Deserialize<List<InstalledAddon>>(json) ?? new List<InstalledAddon>();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load addons.json; starting empty. {ex.Message}");
            _addons = new List<InstalledAddon>();
        }
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataRoot();
            File.WriteAllText(AddonPaths.AddonsFilePath, JsonSerializer.Serialize(_addons, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save addons.json: {ex.Message}");
        }
    }

    /// <summary>Resolve, download and install an addon from a URL or local archive path.</summary>
    public async Task<InstalledAddon> AddAsync(string input, string wowDir, CancellationToken ct = default)
    {
        IAddonSource source = _resolver.Resolve(input);
        _log.Info($"Resolving addon from {input} via {source.GetType().Name}...");

        AddonDownload download = await source.DownloadAsync(input, Http, ct);
        try
        {
            List<string> folders = _installer.InstallFromArchive(download.ArchivePath, wowDir);
            string name = folders.Count == 1 ? folders[0] : (download.SuggestedName ?? folders.FirstOrDefault() ?? "addon");

            InstalledAddon? existing = _addons.FirstOrDefault(a =>
                string.Equals(a.SourceRef, download.SourceRef, StringComparison.OrdinalIgnoreCase));

            InstalledAddon addon = existing ?? new InstalledAddon { Name = name };
            addon.Name = name;
            addon.SourceKind = download.Kind;
            addon.SourceRef = download.SourceRef;
            addon.Version = download.Version;
            addon.Folders = folders;
            addon.InstalledUtc = DateTime.UtcNow;

            if (existing is null)
            {
                _addons.Add(addon);
            }

            Save();
            _log.Info($"Addon '{name}' installed ({folders.Count} folder(s)).");
            return addon;
        }
        finally
        {
            try { File.Delete(download.ArchivePath); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>Delete an addon's installed folders and stop tracking it.</summary>
    public void Remove(InstalledAddon addon, string wowDir)
    {
        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        foreach (string folder in addon.Folders)
        {
            string dir = Path.Combine(addonsDir, folder);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _log.Warn($"Could not delete {dir}: {ex.Message}"); }
            }
        }

        _addons.Remove(addon);
        Save();
        _log.Info($"Removed addon '{addon.Name}'.");
    }
}
