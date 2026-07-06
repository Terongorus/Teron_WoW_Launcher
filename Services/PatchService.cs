using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

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

        File.Copy(exe, backup);
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

        // Only touch disk once the whole image patched successfully. Write to a temp file, then swap.
        string temp = exe + ".tmp";
        File.WriteAllBytes(temp, image);
        File.Copy(temp, exe, overwrite: true);
        File.Delete(temp);
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

        File.Copy(backup, WowExePath(wowDir), overwrite: true);
        _log.Info($"Restored pristine {WowExeFileName} from backup.");
    }

    /// <summary>Best-effort detection of which non-parameterized patches are present in WoW.exe.</summary>
    public HashSet<string> DetectApplied(string wowDir, IReadOnlyList<PatchDefinition> catalog)
        => DetectApplied(File.ReadAllBytes(WowExePath(wowDir)), catalog);

    private static HashSet<string> DetectApplied(byte[] image, IReadOnlyList<PatchDefinition> catalog)
    {
        var applied = new HashSet<string>();
        foreach (PatchDefinition patch in catalog)
        {
            // Parameterized patches vary by value, so the settings file is their source of truth.
            if (patch.Parameter is not null)
            {
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
