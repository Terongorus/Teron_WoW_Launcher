using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TeronWoWLauncher.Models;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Patching;

/// <summary>
/// Applies persistent binary patches to WoW.exe using the "pristine backup + rebuild" model.
///
/// A never-touched copy of the original executable is kept as WoW.exe.backup. Every time the
/// selected patches change, the executable is rebuilt from that pristine copy with the enabled
/// patches applied in a fixed order (Signature Removal, then VanillaTweaks). Because we always
/// start from clean, patches can be toggled freely and can never be applied out of order or twice.
///
/// This class is byte-agnostic: it consumes a catalog of <see cref="PatchDefinition"/> and never
/// hard-codes offsets itself. The image is fully patched in memory and only written to disk once
/// every step has succeeded, so a mid-way failure (e.g. an unexpected client build) never leaves a
/// half-patched executable on disk.
/// </summary>
public sealed class PatchService
{
    public const string WowExeFileName = "WoW.exe";
    public const string BackupFileName = "WoW.exe.backup";

    private readonly Logger _log = Logger.Instance;

    public string WowExePath(string wowDir) => Path.Combine(wowDir, WowExeFileName);
    public string BackupPath(string wowDir) => Path.Combine(wowDir, BackupFileName);
    public bool BackupExists(string wowDir) => File.Exists(BackupPath(wowDir));

    /// <summary>SHA-256 of the current WoW.exe.backup, or null if there isn't one.</summary>
    public string? ComputeBackupHash(string wowDir)
    {
        string backup = BackupPath(wowDir);
        if (!File.Exists(backup))
        {
            return null;
        }

        using FileStream fs = File.OpenRead(backup);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// Ensure a pristine backup exists. If none does, the current WoW.exe is accepted as the pristine
    /// source unless it already looks fully patched by something in the catalog. Returns false (and
    /// logs) if a clean base could not be established.
    /// </summary>
    public bool EnsurePristineBackup(string wowDir, IReadOnlyList<PatchDefinition> catalog)
    {
        string exe = WowExePath(wowDir);
        string backup = BackupPath(wowDir);

        if (File.Exists(backup))
        {
            return true;
        }

        if (!File.Exists(exe))
        {
            _log.Error($"Cannot create backup: {WowExeFileName} not found in {wowDir}.");
            return false;
        }

        byte[] image = File.ReadAllBytes(exe);

        HashSet<string> alreadyApplied = DetectApplied(image, catalog);
        if (alreadyApplied.Count > 0)
        {
            _log.Error(
                $"{WowExeFileName} already appears fully patched ({string.Join(", ", alreadyApplied)}). " +
                "A clean WoW.exe is required to establish the pristine backup.");
            return false;
        }

        try
        {
            AtomicWrite(backup, image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not create pristine backup: {ex.Message}");
            return false;
        }

        _log.Info($"Created pristine backup: {BackupFileName} ({image.Length:N0} bytes).");
        return true;
    }

    /// <summary>
    /// Rebuild WoW.exe from the pristine backup, applying the enabled patches in catalog order.
    /// </summary>
    public void Rebuild(
        string wowDir,
        IReadOnlyList<PatchDefinition> catalog,
        ISet<string> enabledIds,
        IReadOnlyDictionary<string, double?> parameters)
    {
        string exe = WowExePath(wowDir);
        string backup = BackupPath(wowDir);

        if (!File.Exists(backup))
        {
            throw new InvalidOperationException(
                $"No pristine {BackupFileName} to rebuild from. Establish the backup first.");
        }

        CleanupStaleTempFiles(wowDir);

        byte[] image = File.ReadAllBytes(backup);
        _log.Info($"Rebuilding {WowExeFileName} from pristine backup; {enabledIds.Count} patch(es) enabled.");

        foreach (PatchDefinition patch in OrderedForApply(catalog))
        {
            if (!enabledIds.Contains(patch.Id))
            {
                continue;
            }

            parameters.TryGetValue(patch.Id, out double? value);
            ApplyPatch(image, patch, value);
        }

        // Only touch the real path once the whole image is patched successfully, and only via an
        // atomic rename (see AtomicWrite) — never a direct overwrite.
        AtomicWrite(exe, image);
        _log.Info($"{WowExeFileName} rebuilt successfully.");
    }

    /// <summary>Restore the pristine executable, discarding all patches.</summary>
    public void Restore(string wowDir)
    {
        string backup = BackupPath(wowDir);
        if (!File.Exists(backup))
        {
            throw new InvalidOperationException($"No {BackupFileName} to restore from.");
        }

        AtomicWrite(WowExePath(wowDir), File.ReadAllBytes(backup));
        _log.Info($"Restored pristine {WowExeFileName} from backup.");
    }

    /// <summary>
    /// Thin wrapper over <see cref="AtomicFile.WriteAllBytes"/> that upgrades its generic "in use by
    /// another process" message to the specific, actionable one for this context — for WoW.exe that
    /// almost always means the game itself is running. AtomicFile's own UnauthorizedAccessException
    /// wording (permissions/elevation/antivirus) already applies just as well here, so it passes
    /// through unchanged.
    /// </summary>
    private static void AtomicWrite(string dest, byte[] content)
    {
        try
        {
            AtomicFile.WriteAllBytes(dest, content);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Could not write '{Path.GetFileName(dest)}' — it's currently in use, most likely " +
                "because the game is running. Close it and try again.", ex);
        }
    }

    /// <summary>Remove any leftover *.tmp rebuild artifacts from a previous crashed/killed run.</summary>
    private void CleanupStaleTempFiles(string wowDir)
    {
        try
        {
            foreach (string stale in Directory.EnumerateFiles(wowDir, $"{WowExeFileName}.*.tmp"))
            {
                File.Delete(stale);
            }
        }
        catch
        {
            // Best-effort; a leftover temp file doesn't block anything since it's uniquely named.
        }
    }

    /// <summary>
    /// Best-effort detection of which patches are present in WoW.exe, or (for parameterized patches)
    /// whose region simply isn't in a trustworthy pristine state. Used to decide whether an existing
    /// WoW.exe can be trusted as the pristine source for a new backup — always against an
    /// already-in-memory image, never re-reading the file itself.
    /// </summary>
    private static HashSet<string> DetectApplied(byte[] image, IReadOnlyList<PatchDefinition> catalog)
    {
        var applied = new HashSet<string>();
        foreach (PatchDefinition patch in catalog)
        {
            if (patch.Parameter is not null)
            {
                // A parameterized patch's chosen value varies (the settings file is its source of
                // truth for that), but its AcceptBefore fingerprint is the client's fixed pristine
                // default regardless of value - BuildSteps' argument here doesn't affect it. If the
                // region doesn't match that fingerprint, the exe isn't pristine there, even though we
                // can't say what value is currently baked in - flagging it is what lets
                // EnsurePristineBackup refuse instead of silently adopting a non-pristine exe as the
                // backup baseline for this patch (closes a gap the toggle-only check below can't see).
                IReadOnlyList<PatchStep> defSteps = patch.BuildSteps(patch.Parameter.Default);
                bool anyRegionNonPristine = defSteps.Any(s =>
                    s.AcceptBefore is { Length: > 0 } fingerprints &&
                    !fingerprints.Any(fp => RegionEquals(image, checked((int)s.Offset), fp)));
                if (anyRegionNonPristine)
                {
                    applied.Add(patch.Id);
                }

                continue;
            }

            IReadOnlyList<PatchStep> steps = patch.BuildSteps(null);
            if (steps.Count > 0 && steps.All(s => StepApplied(image, s)))
            {
                applied.Add(patch.Id);
            }
        }

        return applied;
    }

    private void ApplyPatch(byte[] image, PatchDefinition patch, double? parameter)
    {
        IReadOnlyList<PatchStep> steps = patch.BuildSteps(parameter);
        _log.Info($"  Applying patch: {patch.Name}");
        foreach (PatchStep step in steps)
        {
            ApplyStep(image, step, patch.Name);
        }
    }

    private static void ApplyStep(byte[] image, PatchStep step, string patchName)
    {
        if (step.Offset >= 0)
        {
            int offset = checked((int)step.Offset);
            if (offset + step.Write.Length > image.Length)
            {
                throw new InvalidOperationException(
                    $"Patch '{patchName}': offset 0x{offset:X} is beyond the end of the file.");
            }

            // Idempotent: already the target value, nothing to do.
            if (RegionEquals(image, offset, step.Write))
            {
                return;
            }

            if (step.AcceptBefore is { Length: > 0 } &&
                !step.AcceptBefore.Any(expected => RegionEquals(image, offset, expected)))
            {
                throw new InvalidOperationException(
                    $"Patch '{patchName}': unexpected bytes at 0x{offset:X}. The client is likely an " +
                    "unsupported build; aborting so the executable is not corrupted.");
            }

            Buffer.BlockCopy(step.Write, 0, image, offset, step.Write.Length);
        }
        else
        {
            if (step.Find is null || step.Find.Length != step.Write.Length)
            {
                throw new InvalidOperationException($"Patch '{patchName}': malformed pattern step.");
            }

            int index = IndexOf(image, step.Find);
            if (index < 0)
            {
                if (IndexOf(image, step.Write) >= 0)
                {
                    return; // already applied
                }

                throw new InvalidOperationException(
                    $"Patch '{patchName}': could not locate its target bytes (unexpected build or a " +
                    "preceding patch changed this region).");
            }

            Buffer.BlockCopy(step.Write, 0, image, index, step.Write.Length);
        }
    }

    private static bool StepApplied(byte[] image, PatchStep step)
    {
        if (step.Offset >= 0)
        {
            return RegionEquals(image, checked((int)step.Offset), step.Write);
        }

        return IndexOf(image, step.Write) >= 0 && (step.Find is null || IndexOf(image, step.Find) < 0);
    }

    private static bool RegionEquals(byte[] image, int offset, byte[] expected)
    {
        if (offset < 0 || offset + expected.Length > image.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (image[offset + i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<PatchDefinition> OrderedForApply(IReadOnlyList<PatchDefinition> catalog)
        => catalog.OrderBy(p => (int)p.Category).ToList();

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        int last = haystack.Length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
