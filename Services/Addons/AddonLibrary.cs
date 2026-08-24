using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Sources;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Addons;

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

    public void Load(string wowDir)
    {
        string path = AddonPaths.AddonsFilePath(wowDir);
        try
        {
            MigrateLegacyGlobalFileIfPresent(wowDir, path);

            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                _addons = JsonSerializer.Deserialize<List<InstalledAddon>>(json) ?? new List<InstalledAddon>();
            }
            else
            {
                _addons = new List<InstalledAddon>();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load addons.json; starting empty. {ex.Message}");
            _addons = new List<InstalledAddon>();
        }
    }

    // One-time migration: every WoW directory used to share one global addons.json
    // (%LocalAppData%\TeronWoWLauncher\addons.json) - which is exactly the bug this fixes, since
    // switching directories never actually changed which addons showed up as tracked. The first
    // directory Load() is called for after upgrading adopts that old file as its own starting
    // point, then the old file is renamed out of the way so it can never be copied into some
    // OTHER, unrelated directory the user points the launcher at later.
    //
    // "First directory to load" is picked by load order, not by which directory the legacy list
    // actually came from - confirmed (2026-08-24) landing an entire other install's tracked addon
    // list (~90 entries) onto a directory that never had any of them installed, the first time an
    // unrelated second directory happened to be the one that loaded after the fix took effect. Every
    // migrated entry is now filtered against what's actually present in the TARGET directory's own
    // Interface\AddOns - an entry only survives if at least one of its Folders exists there, and only
    // those existing folders are kept (a partially-installed multi-folder addon doesn't drag in
    // folders this directory never had). A directory with none of the legacy addons actually
    // installed - the scenario that broke - now correctly ends up with an empty list instead of a
    // fabricated one.
    private void MigrateLegacyGlobalFileIfPresent(string wowDir, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        string legacyPath = AddonPaths.LegacyGlobalAddonsFilePath;
        string migratedMarkerPath = legacyPath + ".migrated";

        // Once the legacy file has been migrated once (renamed to its own .migrated marker), it must
        // never be copied into any OTHER directory again - checked BEFORE touching anything, not just
        // relying on the rename below to fail safely. Confirmed as a real, reproducing bug
        // (2026-07-19): a plain addons.json somehow reappeared at the legacy path after a legitimate
        // first migration had already renamed the original one to its .migrated marker (root cause
        // unconfirmed - likely a leftover from testing an interim build before this per-directory
        // refactor was complete). Every directory created after that silently got a copy of that
        // stale file, because the copy step below always succeeded while the rename step kept failing
        // with "Cannot create a file when that file already exists" against the marker that was
        // already there - the failure was only ever logged as a WARN, not surfaced, and never stopped
        // the harmful copy that had already happened by that point.
        if (File.Exists(migratedMarkerPath) || !File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            string addonsDir = AddonPaths.AddOnsDir(wowDir);
            string json = File.ReadAllText(legacyPath);
            List<InstalledAddon> legacy = JsonSerializer.Deserialize<List<InstalledAddon>>(json) ?? new List<InstalledAddon>();

            var filtered = new List<InstalledAddon>();
            foreach (InstalledAddon addon in legacy)
            {
                List<string> existingFolders = addon.Folders
                    .Where(f => Directory.Exists(Path.Combine(addonsDir, f)))
                    .ToList();
                if (existingFolders.Count > 0)
                {
                    addon.Folders = existingFolders;
                    filtered.Add(addon);
                }
            }

            // Unlike AtomicFile.WriteAllText, Directory.CreateDirectory below doesn't happen on its
            // own - targetPath now lives inside PerDirectoryDataFolder's ".teronwow" subfolder, which
            // may not exist yet for a directory that's never had any per-directory data file before.
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            AtomicFile.WriteAllText(targetPath, JsonSerializer.Serialize(filtered, JsonOptions));
            File.Move(legacyPath, migratedMarkerPath);
            _log.Info(
                $"Migrated the old shared addons.json into {targetPath} (per-directory addon tracking) - " +
                $"kept {filtered.Count} of {legacy.Count} entries that actually exist in {addonsDir}.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not migrate the old shared addons.json: {ex.Message}");
        }
    }

    public void Save(string wowDir)
    {
        try
        {
            AtomicFile.WriteAllText(AddonPaths.AddonsFilePath(wowDir), JsonSerializer.Serialize(_addons, JsonOptions));
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

        AddonDownload download;
        try
        {
            download = await source.DownloadAsync(input, Http, ct);
        }
        catch (Exception ex)
        {
            // Logged here regardless of whether the caller also logs it - AddAsync should be
            // self-contained rather than relying entirely on whichever call site happens to wrap it
            // (currently only MainWindow.AddAddonAsync does). Rethrown unchanged so the existing
            // caller-side toast/behavior is untouched.
            _log.Error($"Failed to download addon from {input}.", ex);
            throw;
        }

        try
        {
            // An existing tracked addon for this same source (a re-install/update) carries any
            // rename the user has made via the UI - passed through so InstallFromDirectory keeps
            // writing to the renamed folder instead of recreating the canonical name.
            InstalledAddon? existing = _addons.FirstOrDefault(a =>
                string.Equals(a.SourceRef, download.SourceRef, StringComparison.OrdinalIgnoreCase));

            // Off the calling thread deliberately - InstallFromDirectory is a plain synchronous
            // File.Copy loop with no await in it anywhere, and AddAsync is always awaited directly
            // from the UI thread (MainWindow.AddAddonAsync). A multi-folder addon (several .toc
            // subfolders in one repo) can otherwise noticeably freeze the window mid-copy - the same
            // bug class already fixed for the client zip's own extraction.
            List<InstalledFolderInfo> folders = await Task.Run(
                () => _installer.InstallFromDirectory(download.ContentDir, wowDir, existing?.FolderRenames), ct);
            InstalledFolderInfo primary =
                folders.FirstOrDefault(f => string.Equals(f.CanonicalFolder, download.SuggestedName, StringComparison.OrdinalIgnoreCase))
                ?? folders[0];

            // Display name and version always come from the addon's own .toc when it has them — this
            // is what makes color-coded titles (e.g. "|cffffcc00Shagu|cffffffffTweaks") and per-addon
            // versions work uniformly across every source, not just manually-adopted addons.
            string name = !string.IsNullOrWhiteSpace(primary.Title) ? primary.Title! : primary.Folder;

            // Matched by SourceRef first (the normal case — re-adding/updating the same URL), but
            // also by folder overlap: a folder that's already tracked under one or more OTHER entries
            // (e.g. each adopted as its own Manual addon from a pre-existing local install, SourceRef
            // null — a repo with several on-demand-loaded modules commonly ends up this way) must all
            // collapse into the single entry this install produces, since installing always overwrites
            // those folders in place. Every matching entry beyond the one kept is removed outright —
            // otherwise the extras become permanently orphaned rows still "claiming" folders that this
            // entry now owns, which is exactly how two addons.json rows end up pointing at the one real
            // AddOns folder.
            var newFolders = folders.Select(f => f.Folder).ToList();
            List<InstalledAddon> matches = _addons.Where(a =>
                string.Equals(a.SourceRef, download.SourceRef, StringComparison.OrdinalIgnoreCase)
                || a.Folders.Intersect(newFolders, StringComparer.OrdinalIgnoreCase).Any())
                .ToList();

            InstalledAddon addon =
                matches.FirstOrDefault(a => string.Equals(a.SourceRef, download.SourceRef, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault()
                ?? new InstalledAddon { Name = name };

            addon.Name = name;
            addon.SourceKind = download.Kind;
            addon.SourceRef = download.SourceRef;
            addon.Version = primary.Version;
            addon.RemoteVersionSignature = download.RemoteVersionSignature;
            addon.Folders = newFolders;
            addon.InstalledUtc = DateTime.UtcNow;
            addon.HasUpdateAvailable = false; // just (re)installed at the latest known signature

            foreach (InstalledAddon duplicate in matches.Where(a => !ReferenceEquals(a, addon)))
            {
                _addons.Remove(duplicate);
            }

            if (!matches.Contains(addon))
            {
                _addons.Add(addon);
            }

            Save(wowDir);
            _log.Info($"Addon '{WowColorTextParser.StripCodes(name)}' installed ({folders.Count} folder(s)).");
            return addon;
        }
        finally
        {
            // Off the calling thread for the same reason the install copy above is - this deletes
            // the exact same temp content the copy just read, so it's just as capable of freezing
            // the window for a large multi-folder addon.
            try { await Task.Run(() => DirectoryHelper.DeleteRecursive(download.ContentDir)); } catch { /* temp cleanup best-effort */ }
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
            Save(wowDir);
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

    public readonly record struct LocalAddonSyncResult(int AutoAdopted, int Merged, IReadOnlyList<LocalAddonCandidate> Conflicts);

    /// <summary>
    /// Scans for untracked local AddOns folders and resolves each one automatically where there's
    /// only one sensible answer:
    ///   * a name that matches no tracked addon is adopted as a new one;
    ///   * a name that matches exactly one already-tracked addon is merged into it (this is almost
    ///     always the same addon reappearing as a second folder - a stray copy, or a folder that
    ///     showed up outside the launcher - not a coincidentally-same-titled different addon), the
    ///     same way <see cref="AddAsync"/> already merges folder overlaps on install;
    ///   * a name that matches two or more tracked addons is genuinely ambiguous and is returned as
    ///     a conflict instead, so the caller can prompt via <see cref="Dialogs.LocalAddonsDialog"/>.
    /// </summary>
    public LocalAddonSyncResult SyncLocalAddons(string wowDir)
    {
        List<LocalAddonCandidate> candidates = ScanForUntracked(wowDir);
        if (candidates.Count == 0)
        {
            return new LocalAddonSyncResult(0, 0, Array.Empty<LocalAddonCandidate>());
        }

        var byName = _addons
            .GroupBy(a => WowColorTextParser.StripCodes(a.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var conflicts = new List<LocalAddonCandidate>();
        int adopted = 0;
        int merged = 0;

        foreach (LocalAddonCandidate candidate in candidates)
        {
            string candidateName = WowColorTextParser.StripCodes(
                string.IsNullOrEmpty(candidate.Title) ? candidate.FolderName : candidate.Title);

            if (byName.TryGetValue(candidateName, out List<InstalledAddon>? matches))
            {
                if (matches.Count > 1)
                {
                    conflicts.Add(candidate);
                    continue;
                }

                InstalledAddon addon = matches[0];
                if (!addon.Folders.Contains(candidate.FolderName, StringComparer.OrdinalIgnoreCase))
                {
                    addon.Folders.Add(candidate.FolderName);
                    merged++;
                }

                continue;
            }

            InstalledAddon adoptedAddon = AdoptCore(candidate, wowDir);
            byName[candidateName] = new List<InstalledAddon> { adoptedAddon };
            adopted++;
        }

        if (adopted > 0 || merged > 0)
        {
            Save(wowDir);
        }

        return new LocalAddonSyncResult(adopted, merged, conflicts);
    }

    /// <summary>Start tracking a folder that's already installed, without downloading anything.</summary>
    public InstalledAddon Adopt(LocalAddonCandidate candidate, string wowDir)
    {
        InstalledAddon addon = AdoptCore(candidate, wowDir);
        Save(wowDir);
        return addon;
    }

    private InstalledAddon AdoptCore(LocalAddonCandidate candidate, string wowDir)
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

        // A folder installed outside the launcher can still be a real git checkout (the user cloned
        // it in by hand) - the launcher's own GitHub/GitLab installs never leave a ".git" folder
        // behind (see GitHubAddonSource.CopyWorkingTree), so finding one here means this was adopted,
        // not installed through us. Link it to its real remote immediately instead of leaving it
        // stuck as an untrackable "Local" addon forever.
        string folderPath = Path.Combine(AddonPaths.AddOnsDir(wowDir), candidate.FolderName);
        if (TryResolveGitRemote(folderPath) is { } git)
        {
            addon.SourceKind = git.Kind;
            addon.SourceRef = git.SourceRef;
            addon.RemoteVersionSignature = git.HeadSha;
            _log.Info($"Linked local addon '{WowColorTextParser.StripCodes(addon.Name)}' to its git remote ({git.SourceRef}).");
        }

        _addons.Add(addon);
        _log.Info($"Adopted local addon '{WowColorTextParser.StripCodes(addon.Name)}' ({candidate.FolderName}).");
        return addon;
    }

    /// <summary>
    /// Re-checks every already-tracked Manual addon's own folder for a resolvable git remote that
    /// wasn't linked at adoption time (adopted before this detection existed, or the user ran
    /// `git init`/added a remote to an existing manual folder afterward). Upgrades SourceKind/
    /// SourceRef/RemoteVersionSignature in place; never touches an addon already tracked through a
    /// real source. Pure local disk I/O (opens the existing ".git" folder, no network) - safe to run
    /// from the local-only refresh path as well as the full one.
    /// </summary>
    public bool LinkManualAddonsWithGitRemotes(string wowDir)
    {
        bool changed = false;
        string addonsDir = AddonPaths.AddOnsDir(wowDir);

        foreach (InstalledAddon addon in _addons)
        {
            if (addon.SourceKind != AddonSourceKind.Manual)
            {
                continue;
            }

            string? folder = addon.Folders.FirstOrDefault();
            if (folder is null || TryResolveGitRemote(Path.Combine(addonsDir, folder)) is not { } git)
            {
                continue;
            }

            addon.SourceKind = git.Kind;
            addon.SourceRef = git.SourceRef;
            addon.RemoteVersionSignature = git.HeadSha;
            changed = true;
            _log.Info($"Linked previously-manual addon '{WowColorTextParser.StripCodes(addon.Name)}' to its git remote ({git.SourceRef}).");
        }

        if (changed)
        {
            Save(wowDir);
        }

        return changed;
    }

    private static readonly Regex GitHubRemoteUrlRegex =
        new(@"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GitLabRemoteUrlRegex =
        new(@"gitlab\.com[:/](?<path>[^\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads a local addon folder's own ".git" checkout (if it has one) and, if its "origin" remote
    /// points at a host this app already knows how to track (github.com/gitlab.com), returns the
    /// same SourceKind/SourceRef shape <see cref="AddAsync"/> would have stored for a normal tracked
    /// install - plus the commit currently checked out as the immediate RemoteVersionSignature
    /// baseline, so the very first update check compares against reality instead of null (which
    /// would otherwise read as "always outdated" for GitLab - see CheckForUpdatesAsync's GitHub-only
    /// null/format migration branch, which a fresh git-linked addon isn't going through).
    /// </summary>
    private static (AddonSourceKind Kind, string SourceRef, string HeadSha)? TryResolveGitRemote(string addonDir)
    {
        if (!Repository.IsValid(addonDir))
        {
            return null;
        }

        try
        {
            using var repo = new Repository(addonDir);
            string? remoteUrl = repo.Network.Remotes["origin"]?.Url;
            string? headSha = repo.Head.Tip?.Sha;
            if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(headSha))
            {
                return null;
            }

            Match gh = GitHubRemoteUrlRegex.Match(remoteUrl);
            if (gh.Success)
            {
                return (AddonSourceKind.GitHub, $"https://github.com/{gh.Groups["owner"].Value}/{gh.Groups["repo"].Value}", headSha);
            }

            Match gl = GitLabRemoteUrlRegex.Match(remoteUrl);
            if (gl.Success)
            {
                return (AddonSourceKind.GitLab, $"https://gitlab.com/{gl.Groups["path"].Value}", headSha);
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Instance.Debug($"Could not read git remote info for '{addonDir}': {ex.Message}");
            return null;
        }
    }

    public enum RenameFolderOutcome
    {
        Success,
        InvalidName,
        Collision,
        MoveFailed,
    }

    /// <summary>
    /// Renames one of an addon's installed folders on disk, and records the rename so a future
    /// install/update (see <see cref="AddAsync"/> and <c>AddonInstaller.InstallFromDirectory</c>)
    /// keeps writing to the renamed name instead of recreating the canonical one. <see cref="
    /// InstalledAddon.SourceRef"/> is untouched, so update-checking keeps comparing against the same
    /// remote regardless of the rename.
    /// </summary>
    public RenameFolderOutcome RenameFolder(InstalledAddon addon, string oldFolder, string newFolderName, string wowDir)
    {
        string trimmed = newFolderName.Trim();
        if (trimmed.Length == 0 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return RenameFolderOutcome.InvalidName;
        }

        if (string.Equals(trimmed, oldFolder, StringComparison.OrdinalIgnoreCase))
        {
            return RenameFolderOutcome.Success; // no-op
        }

        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        string oldPath = Path.Combine(addonsDir, oldFolder);
        string newPath = Path.Combine(addonsDir, trimmed);

        bool collides = Directory.Exists(newPath) ||
            _addons.Any(a => !ReferenceEquals(a, addon) && a.Folders.Contains(trimmed, StringComparer.OrdinalIgnoreCase));
        if (collides)
        {
            return RenameFolderOutcome.Collision;
        }

        if (!Directory.Exists(oldPath))
        {
            return RenameFolderOutcome.MoveFailed;
        }

        try
        {
            Directory.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to rename addon folder '{oldFolder}' to '{trimmed}': {ex.Message}");
            return RenameFolderOutcome.MoveFailed;
        }

        int index = addon.Folders.FindIndex(f => string.Equals(f, oldFolder, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            addon.Folders[index] = trimmed;
        }

        // Keyed by the canonical (repo/.toc-derived) name, not whatever the folder was previously
        // renamed to, so a second rename still resolves back to the one original canonical entry
        // instead of accumulating a rename chain that InstallFromDirectory can't follow.
        string canonical = addon.FolderRenames
            .FirstOrDefault(kv => string.Equals(kv.Value, oldFolder, StringComparison.OrdinalIgnoreCase)).Key
            ?? oldFolder;
        addon.FolderRenames[canonical] = trimmed;

        Save(wowDir);
        _log.Info($"Renamed addon folder '{oldFolder}' to '{trimmed}'.");
        return RenameFolderOutcome.Success;
    }

    /// <summary>
    /// Checks every non-Manual tracked addon against its source for a newer version, setting
    /// <see cref="InstalledAddon.HasUpdateAvailable"/> on each. No download happens here.
    /// </summary>
    public async Task CheckForUpdatesAsync(string wowDir, CancellationToken ct = default)
    {
        bool migrated = false;

        foreach (InstalledAddon addon in _addons)
        {
            if (addon.IgnoreUpdates || addon.SourceKind == AddonSourceKind.Manual || string.IsNullOrWhiteSpace(addon.SourceRef))
            {
                addon.HasUpdateAvailable = false;
                continue;
            }

            try
            {
                IAddonSource source = _resolver.Resolve(addon.SourceRef);
                string? latest = await source.GetLatestVersionSignatureAsync(addon.SourceRef, Http, ct);
                if (latest is null)
                {
                    addon.HasUpdateAvailable = false;
                    continue;
                }

                if (addon.SourceKind == AddonSourceKind.GitHub && !LooksLikeCommitSha(addon.RemoteVersionSignature))
                {
                    // One-time transition from the pre-git-clone signature format (a release tag, a
                    // "branch:name[@sha]" string, or simply null from an addon added before this
                    // field even existed) to a plain commit sha — adopt it silently as the new
                    // baseline instead of flagging every already-tracked addon as updatable the
                    // moment the signature format itself changes, with nothing about the addon
                    // actually different. Normal sha-vs-sha comparison takes over from here on.
                    //
                    // Scoped to GitHub specifically: every non-GitHub source's signature format
                    // (size+ETag/Last-Modified style, see DirectArchiveAddonSource) never looks like a
                    // 40-char commit sha, so without this scoping check every Archive-kind addon would
                    // hit this branch on *every* check forever and never actually compare — silently
                    // never detecting an update. Found while adding the Legacy-WoW/Warperia sources,
                    // which would have inherited the same silent breakage.
                    addon.RemoteVersionSignature = latest;
                    addon.HasUpdateAvailable = false;
                    migrated = true;
                    continue;
                }

                addon.HasUpdateAvailable = !string.Equals(latest, addon.RemoteVersionSignature, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                _log.Debug($"Update check failed for '{WowColorTextParser.StripCodes(addon.Name)}': {ex.Message}");
                addon.HasUpdateAvailable = false;
            }
        }

        if (migrated)
        {
            Save(wowDir);
        }
    }

    private static bool LooksLikeCommitSha(string? s) => s is { Length: 40 } && s.All(Uri.IsHexDigit);

    /// <summary>Delete an addon's installed folders, its cached git clone (if any), and stop tracking it.</summary>
    public async Task RemoveAsync(InstalledAddon addon, string wowDir)
    {
        string addonsDir = AddonPaths.AddOnsDir(wowDir);

        // Off the calling thread - a GitHub addon's cached git clone can hold a sizeable object
        // database, and this is always called directly from a UI click handler, so deleting it (plus
        // the installed AddOns folder(s)) synchronously would freeze the window for however long that
        // takes, same reasoning as AddAsync's own install-copy/temp-cleanup backgrounding above.
        await Task.Run(() =>
        {
            foreach (string folder in addon.Folders)
            {
                string dir = Path.Combine(addonsDir, folder);
                if (Directory.Exists(dir))
                {
                    try { DirectoryHelper.DeleteRecursive(dir); }
                    catch (Exception ex) { _log.Warn($"Could not delete {dir}: {ex.Message}"); }
                }
            }

            if (addon.SourceKind == AddonSourceKind.GitHub && TryParseGitHubRepo(addon.SourceRef, out string owner, out string repo))
            {
                string cacheDir = AddonPaths.AddonRepoCacheDir(owner, repo);
                if (Directory.Exists(cacheDir))
                {
                    try { DirectoryHelper.DeleteRecursive(cacheDir); }
                    catch (Exception ex) { _log.Warn($"Could not delete cached repo clone {cacheDir}: {ex.Message}"); }
                }
            }
        });

        _addons.Remove(addon);
        Save(wowDir);
        _log.Info($"Removed addon '{WowColorTextParser.StripCodes(addon.Name)}'.");
    }

    /// <summary>
    /// Best-effort "what is this addon" text for the Details button, in priority order: the GitHub
    /// repo's own README (for addons tracked from GitHub), else a local README file sitting in the
    /// addon's own folder, else whatever the .toc file itself carries (title/notes/author/etc.) —
    /// the last resort for out-of-support, never-GitHub-tracked, or locally-developed addons.
    /// </summary>
    public async Task<string> GetDetailsMarkdownAsync(InstalledAddon addon, string wowDir, CancellationToken ct)
    {
        if (addon.SourceKind == AddonSourceKind.GitHub && TryParseGitHubRepo(addon.SourceRef, out string owner, out string repo))
        {
            string? readme = await TryFetchGitHubReadmeAsync(owner, repo, ct);
            if (readme is not null)
            {
                return readme;
            }
        }

        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        foreach (string folder in addon.Folders)
        {
            string? localReadme = TryReadLocalReadme(Path.Combine(addonsDir, folder));
            if (localReadme is not null)
            {
                return localReadme;
            }
        }

        return BuildTocFallbackMarkdown(addon, addonsDir);
    }

    private static bool TryParseGitHubRepo(string? sourceRef, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return false;
        }

        Match m = Regex.Match(sourceRef, @"github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return false;
        }

        owner = m.Groups["owner"].Value;
        repo = m.Groups["repo"].Value;
        return true;
    }

    // GitHub's "get README" endpoint auto-detects the actual filename/casing (README.md, Readme.txt,
    // etc.), so there's no need to guess — it returns base64-encoded content regardless of format.
    private async Task<string?> TryFetchGitHubReadmeAsync(string owner, string repo, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/readme", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("content", out JsonElement contentEl))
            {
                return null;
            }

            string base64 = (contentEl.GetString() ?? string.Empty).Replace("\n", string.Empty);
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (Exception ex)
        {
            _log.Debug($"GitHub README fetch failed for {owner}/{repo}: {ex.Message}");
            return null;
        }
    }

    private static string? TryReadLocalReadme(string folderDir)
    {
        if (!Directory.Exists(folderDir))
        {
            return null;
        }

        // Prefer a .md file if the folder happens to have more than one README-ish file.
        string? path = Directory.EnumerateFiles(folderDir, "README*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (path is null)
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTocFallbackMarkdown(InstalledAddon addon, string addonsDir)
    {
        var sections = new List<string>();
        foreach (string folder in addon.Folders)
        {
            string dir = Path.Combine(addonsDir, folder);
            string? tocPath = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.toc").FirstOrDefault() : null;
            if (tocPath is null)
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadLines(tocPath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("##", StringComparison.Ordinal))
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon < 2)
                {
                    continue;
                }

                string key = line[2..colon].Trim();
                string value = WowColorTextParser.StripCodes(line[(colon + 1)..].Trim());
                if (key.Length > 0 && value.Length > 0)
                {
                    fields[key] = value;
                }
            }

            if (fields.Count == 0)
            {
                continue;
            }

            sections.Add($"## {folder}\n\n" + string.Join('\n', fields.Select(f => $"**{f.Key}:** {f.Value}  ")));
        }

        return sections.Count > 0
            ? string.Join("\n\n", sections)
            : "No details available — this addon has no GitHub source, no README file, and its .toc carries no extra information.";
    }
}
