using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Dlls;
using TeronWoWLauncher.Services.Patching;
namespace TeronWoWLauncher.Services.Launch;

/// <summary>Ok covers "no backup yet" and "first time this check has ever run" (nothing to compare against) as well as a genuine match.</summary>
public enum BackupIntegrityStatus { Ok, Mismatch }

/// <summary>
/// <paramref name="ProcessId"/> is set whenever a game process was actually created, even if a later
/// step (e.g. auto-login) had trouble — the caller can use it to track "is the game we just launched
/// still alive" via <see cref="System.Diagnostics.Process.GetProcessById"/>, which is far more
/// reliable right after launch than re-scanning and matching by module path (see
/// <see cref="GameProcessService"/>'s own doc comment on why that can throw/return a false negative
/// for a process still settling from injection).
/// </summary>
public sealed record PlayResult(bool Success, int? ProcessId);

/// <summary>
/// Ties the individual services together into the single, fixed-order "Play" flow:
///   1-2. rebuild WoW.exe from the pristine backup with the enabled executable patches
///        (Signature Removal, then VanillaTweaks),
///   3-4. resolve dlls.txt and launch the game suspended, injecting each DLL, then resume,
///   5.   auto-login,
///   (6.  custom MPQ patches are managed separately and simply load with the patched client).
/// </summary>
public sealed class LaunchOrchestrator
{
    private readonly Logger _log = Logger.Instance;
    private readonly SettingsService _settings;
    private readonly DirectorySettingsService _dirSettings;
    private readonly PatchService _patch = new();
    private readonly DllListService _dlls = new();
    private readonly GameInjector _injector = new();
    private readonly AutoLoginService _autoLogin = new();
    private readonly GameProcessService _processCheck = new();

    // Serializes every executable-patch rebuild (Play and apply-on-change both go through this),
    // so overlapping calls can never race on the same temp file or on WoW.exe itself.
    private readonly SemaphoreSlim _patchGate = new(1, 1);

    // dirSettings is a shared instance owned by MainWindow, kept loaded for whichever WoW directory
    // is currently selected (reloaded on every directory switch, same as AddonLibrary's _addons) -
    // every method below trusts dirSettings.Current already reflects the wowDir it's given/resolves.
    public LaunchOrchestrator(SettingsService settings, DirectorySettingsService dirSettings)
    {
        _settings = settings;
        _dirSettings = dirSettings;
    }

    /// <param name="onProcessLaunched">
    /// Fired as soon as the game process exists and every DLL is injected — before auto-login runs,
    /// which can take several more seconds waiting for the window/loading screen. Lets the caller
    /// start treating the game as "running" (multi-instance guard, minimize-on-launch) the moment
    /// that's actually true, instead of only after the whole method returns.
    /// </param>
    public async Task<PlayResult> PlayAsync(
        IProgress<string>? progress = null, Action<int>? onProcessLaunched = null, CancellationToken ct = default)
    {
        void Report(string message)
        {
            _log.Info(message);
            progress?.Report(message);
        }

        string wowDir = _settings.ResolveWowDirectory();
        string wowExe = _settings.ResolveWowExePath();

        if (!File.Exists(wowExe))
        {
            _log.Error($"WoW.exe not found at {wowExe}. Set the game folder in Settings.");
            return new PlayResult(false, null);
        }

        // 1-2. Executable patches, always rebuilt from the pristine backup so order is guaranteed.
        try
        {
            await SyncExecutablePatchesGuardedAsync(wowDir, Report);
        }
        catch (Exception ex)
        {
            _log.Error("Executable patching failed; launching the current executable unpatched.", ex);
        }

        // 3-4. Resolve the DLL list and inject at launch. (realmlist.wtf is written when the user
        // saves settings, not here — no need to rewrite it every launch.)
        List<string> injectList = _dlls.GetInjectionList(wowDir);
        Report($"Prepared {injectList.Count} DLL(s) for injection.");

        LaunchResult result;
        try
        {
            result = await Task.Run(() => _injector.Launch(wowExe, injectList), ct);
        }
        catch (Exception ex)
        {
            _log.Error("Launch/injection failed.", ex);
            return new PlayResult(false, null);
        }

        // Remember the accepted DLL list so the UI can flag changes next time.
        _dlls.UpdateCache(wowDir, injectList);

        // The process is confirmed alive and fully injected at this point, even though auto-login
        // (which can take several more seconds) hasn't run yet.
        onProcessLaunched?.Invoke(result.ProcessId);

        // 5. Auto-login.
        DirectorySettings s = _dirSettings.Current;
        if (s.AutoLoginEnabled && !string.IsNullOrEmpty(s.Account))
        {
            string password = _dirSettings.GetPassword();

            // A Direct3D9 hook/translation DLL (most commonly DXVK's d3d9.dll) can take noticeably
            // longer than the native renderer to stand up its device and show the game window, since
            // it's building its own instance/device (Vulkan, for DXVK) underneath first - give
            // auto-login extra patience in that case instead of giving up early with "game window
            // not found before timeout".
            bool hasD3D9Hook = injectList.Any(path =>
                string.Equals(Path.GetFileName(path), "d3d9.dll", StringComparison.OrdinalIgnoreCase));
            int loginTimeoutMs = hasD3D9Hook ? AutoLoginService.ExtendedTimeoutMs : AutoLoginService.DefaultTimeoutMs;

            await _autoLogin.PerformLoginAsync(result.ProcessId, s.Account, password, s.LoginDelayMs, loginTimeoutMs, ct);
        }
        else
        {
            _log.Info("Auto-login disabled or no account set; skipping.");
        }

        Report("Launch complete.");
        return new PlayResult(true, result.ProcessId);
    }

    /// <summary>
    /// Apply the current patch selection to WoW.exe right away (used when the user toggles or adjusts
    /// a patch in the UI, so launching stays instant). No-op when the game folder / WoW.exe isn't set.
    /// The rebuild runs off the UI thread; the skip-if-unchanged check keeps repeat calls cheap.
    /// </summary>
    public async Task ApplyPatchesAsync(IProgress<string>? progress = null)
    {
        string wowDir = _settings.ResolveWowDirectory();
        if (!File.Exists(_patch.WowExePath(wowDir)))
        {
            return;
        }

        try
        {
            await SyncExecutablePatchesGuardedAsync(wowDir, message =>
            {
                _log.Info(message);
                progress?.Report(message);
            });
        }
        catch (Exception ex)
        {
            _log.Error("Applying patches failed.", ex);
        }
    }

    /// <summary>
    /// Verifies WoW.exe.backup still matches the hash recorded when it was created, so disk
    /// corruption, an interrupted write, or external tampering doesn't silently keep getting used as
    /// the "pristine" source for every future patch rebuild. If no hash has ever been recorded yet
    /// (e.g. a backup that predates this check), the current hash is adopted as the baseline rather
    /// than flagging a false mismatch on a backup nothing has actually verified before.
    /// </summary>
    public BackupIntegrityStatus VerifyPristineBackupIntegrity(string wowDir)
    {
        if (!_patch.BackupExists(wowDir))
        {
            return BackupIntegrityStatus.Ok;
        }

        string? currentHash = _patch.ComputeBackupHash(wowDir);
        string? storedHash = _dirSettings.Current.PristineBackupHash;

        if (storedHash is null)
        {
            _dirSettings.Current.PristineBackupHash = currentHash;
            _dirSettings.Save(wowDir);
            return BackupIntegrityStatus.Ok;
        }

        return string.Equals(currentHash, storedHash, StringComparison.OrdinalIgnoreCase)
            ? BackupIntegrityStatus.Ok
            : BackupIntegrityStatus.Mismatch;
    }

    /// <summary>
    /// Discards a backup that failed integrity verification, along with its recorded hash and the
    /// applied-patch signature, so the next patch rebuild re-establishes both fresh (from whatever
    /// WoW.exe is on disk at that point — e.g. right after a client repair).
    /// </summary>
    public void DiscardCorruptBackup(string wowDir)
    {
        try { File.Delete(_patch.BackupPath(wowDir)); }
        catch (Exception ex) { _log.Warn($"Could not delete corrupt {PatchService.BackupFileName}: {ex.Message}"); }

        _dirSettings.Current.PristineBackupHash = null;
        _dirSettings.Current.AppliedPatchSignature = null;
        _dirSettings.Save(wowDir);
    }

    /// <summary>
    /// Runs <see cref="SyncExecutablePatches"/> off the UI thread, serialized against every other
    /// caller (Play and apply-on-change alike) via <see cref="_patchGate"/>, and skips cleanly
    /// instead of throwing when the game is currently running (WoW.exe would be locked).
    /// </summary>
    private async Task SyncExecutablePatchesGuardedAsync(string wowDir, Action<string> report)
    {
        await _patchGate.WaitAsync();
        try
        {
            if (_processCheck.IsRunning(wowDir))
            {
                report("WoW is currently running — close the game to apply executable patch changes.");
                return;
            }

            await Task.Run(() => SyncExecutablePatches(wowDir, report));
        }
        finally
        {
            _patchGate.Release();
        }
    }

    private void SyncExecutablePatches(string wowDir, Action<string> report)
    {
        IReadOnlyList<PatchDefinition> catalog = PatchCatalog.All;
        var enabled = new HashSet<string>(
            _dirSettings.Current.EnabledPatchIds.Where(id => catalog.Any(p => p.Id == id)));

        string desiredSignature = BuildPatchSignature(catalog, enabled);
        string appliedSignature = _dirSettings.Current.AppliedPatchSignature ?? string.Empty;
        bool backupReady = enabled.Count == 0 || _patch.BackupExists(wowDir);

        // Skip entirely when the on-disk executable already reflects the current selection — no
        // rebuild happens every launch, only when the patch selection actually changes.
        if (backupReady && desiredSignature == appliedSignature && File.Exists(_patch.WowExePath(wowDir)))
        {
            report("Executable patches already up to date; skipping.");
            return;
        }

        if (enabled.Count > 0)
        {
            bool backupExistedBefore = _patch.BackupExists(wowDir);
            if (!_patch.EnsurePristineBackup(wowDir, catalog))
            {
                throw new InvalidOperationException("Could not establish a pristine WoW.exe backup.");
            }

            // Record the hash only at the moment the backup is actually (re)created — it never
            // changes after that, so this only ever runs once per backup's lifetime, not every rebuild.
            if (!backupExistedBefore)
            {
                _dirSettings.Current.PristineBackupHash = _patch.ComputeBackupHash(wowDir);
            }

            Dictionary<string, double?> parameters = catalog.ToDictionary(p => p.Id, EffectiveParam);

            report($"Patch selection changed — rebuilding WoW.exe with {enabled.Count} patch(es) from pristine backup...");
            _patch.Rebuild(wowDir, catalog, enabled, parameters);
        }
        else if (_patch.BackupExists(wowDir) && !FilesEqual(_patch.WowExePath(wowDir), _patch.BackupPath(wowDir)))
        {
            // Everything was turned off but the exe still differs from pristine — revert it.
            report("No executable patches selected; restoring pristine WoW.exe.");
            _patch.Restore(wowDir);
        }

        // Record what is now on disk so subsequent launches can skip when unchanged.
        _dirSettings.Current.AppliedPatchSignature = desiredSignature;
        _dirSettings.Save(wowDir);
    }

    private double? EffectiveParam(PatchDefinition patch)
    {
        if (patch.Parameter is null)
        {
            return null;
        }

        return _dirSettings.Current.PatchParameters.TryGetValue(patch.Id, out double v) ? v : patch.Parameter.Default;
    }

    private string BuildPatchSignature(IReadOnlyList<PatchDefinition> catalog, HashSet<string> enabled)
    {
        Dictionary<string, PatchDefinition> byId = catalog.ToDictionary(p => p.Id);
        var parts = new List<string>();
        foreach (string id in enabled.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out PatchDefinition? p) && p.Parameter is not null)
            {
                double v = EffectiveParam(p) ?? p.Parameter.Default;
                parts.Add($"{id}={v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else
            {
                parts.Add(id);
            }
        }

        return string.Join(",", parts);
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length)
        {
            return false;
        }

        using FileStream sa = fa.OpenRead();
        using FileStream sb = fb.OpenRead();
        const int chunk = 1 << 16;
        byte[] ba = new byte[chunk];
        byte[] bb = new byte[chunk];
        int read;
        while ((read = sa.Read(ba, 0, chunk)) > 0)
        {
            int readB = 0;
            while (readB < read)
            {
                int n = sb.Read(bb, readB, read - readB);
                if (n == 0)
                {
                    return false;
                }

                readB += n;
            }

            for (int i = 0; i < read; i++)
            {
                if (ba[i] != bb[i])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
