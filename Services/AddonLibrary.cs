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
    private readonly LocalAddonScanner _localScanner = new();

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
            List<InstalledFolderInfo> folders = _installer.InstallFromArchive(download.ArchivePath, wowDir);
            InstalledFolderInfo primary =
                folders.FirstOrDefault(f => string.Equals(f.Folder, download.SuggestedName, StringComparison.OrdinalIgnoreCase))
                ?? folders[0];

            // Display name and version always come from the addon's own .toc when it has them — this
            // is what makes color-coded titles (e.g. "|cffffcc00Shagu|cffffffffTweaks") and per-addon
            // versions work uniformly across every source, not just manually-adopted addons.
            string name = !string.IsNullOrWhiteSpace(primary.Title) ? primary.Title! : primary.Folder;

            InstalledAddon? existing = _addons.FirstOrDefault(a =>
                string.Equals(a.SourceRef, download.SourceRef, StringComparison.OrdinalIgnoreCase));

            InstalledAddon addon = existing ?? new InstalledAddon { Name = name };
            addon.Name = name;
            addon.SourceKind = download.Kind;
            addon.SourceRef = download.SourceRef;
            addon.Version = primary.Version;
            addon.RemoteVersionSignature = download.RemoteVersionSignature;
            addon.Folders = folders.Select(f => f.Folder).ToList();
            addon.InstalledUtc = DateTime.UtcNow;
            addon.HasUpdateAvailable = false; // just (re)installed at the latest known signature

            if (existing is null)
            {
                _addons.Add(addon);
            }

            Save();
            _log.Info($"Addon '{WowColorTextParser.StripCodes(name)}' installed ({folders.Count} folder(s)).");
            return addon;
        }
        finally
        {
            try { File.Delete(download.ArchivePath); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Re-reads each tracked addon's Name/Version from its own .toc on disk, in case files changed
    /// outside the launcher. Cheap (no network) — safe to call on every Refresh.
    /// </summary>
    public void RefreshMetadataFromDisk(string wowDir)
    {
        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        bool changed = false;

        foreach (InstalledAddon addon in _addons)
        {
            string? primaryFolder = addon.Folders.FirstOrDefault();
            if (primaryFolder is null)
            {
                continue;
            }

            (string? title, string? version) = TocMetadataReader.Read(Path.Combine(addonsDir, primaryFolder));
            string newName = !string.IsNullOrWhiteSpace(title) ? title! : primaryFolder;
            if (newName != addon.Name || version != addon.Version)
            {
                addon.Name = newName;
                addon.Version = version;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>
    /// AddOns folders on disk that aren't tracked yet (installed by hand, or predating the addon
    /// manager). Every folder across every tracked addon is excluded, not just each entry's own name.
    /// </summary>
    public List<LocalAddonCandidate> ScanForUntracked(string wowDir)
    {
        IEnumerable<string> trackedFolders = _addons.SelectMany(a => a.Folders);
        return _localScanner.Scan(wowDir, trackedFolders);
    }

    public readonly record struct LocalAddonSyncResult(int AutoAdopted, IReadOnlyList<LocalAddonCandidate> Conflicts);

    /// <summary>
    /// Scans for untracked local AddOns folders and adopts each one automatically, UNLESS its name
    /// matches an already-tracked addon (e.g. the same addon exists both as a hand-installed folder and
    /// a GitHub-tracked one under a different folder name) — those are returned as conflicts instead of
    /// being silently adopted, so the caller can prompt the user via <see cref="Dialogs.LocalAddonsDialog"/>.
    /// </summary>
    public LocalAddonSyncResult SyncLocalAddons(string wowDir)
    {
        List<LocalAddonCandidate> candidates = ScanForUntracked(wowDir);
        if (candidates.Count == 0)
        {
            return new LocalAddonSyncResult(0, Array.Empty<LocalAddonCandidate>());
        }

        var existingNames = new HashSet<string>(
            _addons.Select(a => WowColorTextParser.StripCodes(a.Name)),
            StringComparer.OrdinalIgnoreCase);

        var conflicts = new List<LocalAddonCandidate>();
        int adopted = 0;

        foreach (LocalAddonCandidate candidate in candidates)
        {
            string candidateName = WowColorTextParser.StripCodes(
                string.IsNullOrEmpty(candidate.Title) ? candidate.FolderName : candidate.Title);

            if (existingNames.Contains(candidateName))
            {
                conflicts.Add(candidate);
                continue;
            }

            AdoptCore(candidate);
            existingNames.Add(candidateName);
            adopted++;
        }

        if (adopted > 0)
        {
            Save();
        }

        return new LocalAddonSyncResult(adopted, conflicts);
    }

    /// <summary>Start tracking a folder that's already installed, without downloading anything.</summary>
    public InstalledAddon Adopt(LocalAddonCandidate candidate)
    {
        InstalledAddon addon = AdoptCore(candidate);
        Save();
        return addon;
    }

    private InstalledAddon AdoptCore(LocalAddonCandidate candidate)
    {
        var addon = new InstalledAddon
        {
            Name = string.IsNullOrEmpty(candidate.Title) ? candidate.FolderName : candidate.Title,
            SourceKind = AddonSourceKind.Manual,
            SourceRef = null,
            Version = candidate.Version,
            Folders = new List<string> { candidate.FolderName },
            InstalledUtc = DateTime.UtcNow,
        };

        _addons.Add(addon);
        _log.Info($"Adopted local addon '{WowColorTextParser.StripCodes(addon.Name)}' ({candidate.FolderName}).");
        return addon;
    }

    /// <summary>
    /// Checks every non-Manual tracked addon against its source for a newer version, setting
    /// <see cref="InstalledAddon.HasUpdateAvailable"/> on each. No download happens here.
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        foreach (InstalledAddon addon in _addons)
        {
            if (addon.SourceKind == AddonSourceKind.Manual || string.IsNullOrWhiteSpace(addon.SourceRef))
            {
                addon.HasUpdateAvailable = false;
                continue;
            }

            try
            {
                IAddonSource source = _resolver.Resolve(addon.SourceRef);
                string? latest = await source.GetLatestVersionSignatureAsync(addon.SourceRef, Http, ct);
                addon.HasUpdateAvailable = latest is not null
                    && !string.IsNullOrEmpty(addon.RemoteVersionSignature)
                    && !string.Equals(latest, addon.RemoteVersionSignature, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                _log.Debug($"Update check failed for '{WowColorTextParser.StripCodes(addon.Name)}': {ex.Message}");
                addon.HasUpdateAvailable = false;
            }
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
        _log.Info($"Removed addon '{WowColorTextParser.StripCodes(addon.Name)}'.");
    }
}
