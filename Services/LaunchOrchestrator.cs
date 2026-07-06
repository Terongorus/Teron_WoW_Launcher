using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

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
    private readonly PatchService _patch = new();
    private readonly DllListService _dlls = new();
    private readonly GameInjector _injector = new();
    private readonly AutoLoginService _autoLogin = new();

    public LaunchOrchestrator(SettingsService settings) => _settings = settings;

    public async Task<bool> PlayAsync(IProgress<string>? progress = null, CancellationToken ct = default)
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
            return false;
        }

        // 1-2. Executable patches, always rebuilt from the pristine backup so order is guaranteed.
        try
        {
            SyncExecutablePatches(wowDir, Report);
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
            return false;
        }

        // Remember the accepted DLL list so the UI can flag changes next time.
        _dlls.UpdateCache(wowDir, injectList);

        // 5. Auto-login.
        LauncherSettings s = _settings.Current;
        if (s.AutoLoginEnabled && !string.IsNullOrEmpty(s.Account))
        {
            string password = _settings.GetPassword();
            await _autoLogin.PerformLoginAsync(result.ProcessId, s.Account, password, s.LoginDelayMs, ct: ct);
        }
        else
        {
            _log.Info("Auto-login disabled or no account set; skipping.");
        }

        Report("Launch complete.");
        return true;
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

        await Task.Run(() =>
        {
            try
            {
                SyncExecutablePatches(wowDir, message =>
                {
                    _log.Info(message);
                    progress?.Report(message);
                });
            }
            catch (Exception ex)
            {
                _log.Error("Applying patches failed.", ex);
            }
        });
    }

    private void SyncExecutablePatches(string wowDir, Action<string> report)
    {
        IReadOnlyList<PatchDefinition> catalog = PatchCatalog.All;
        var enabled = new HashSet<string>(
            _settings.Current.EnabledPatchIds.Where(id => catalog.Any(p => p.Id == id)));

        string desiredSignature = BuildPatchSignature(catalog, enabled);
        string appliedSignature = _settings.Current.AppliedPatchSignature ?? string.Empty;
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
            if (!_patch.EnsurePristineBackup(wowDir, catalog))
            {
                throw new InvalidOperationException("Could not establish a pristine WoW.exe backup.");
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
        _settings.Current.AppliedPatchSignature = desiredSignature;
        _settings.Save();
    }

    private double? EffectiveParam(PatchDefinition patch)
    {
        if (patch.Parameter is null)
        {
            return null;
        }

        return _settings.Current.PatchParameters.TryGetValue(patch.Id, out double v) ? v : patch.Parameter.Default;
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
