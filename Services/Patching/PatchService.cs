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

    public static string WowExePath(string wowDir) => Path.Combine(wowDir, WowExeFileName);
    public static string BackupPath(string wowDir) => Path.Combine(wowDir, BackupFileName);
    public static bool BackupExists(string wowDir) => File.Exists(BackupPath(wowDir));

    /// <summary>SHA-256 of the current WoW.exe.backup, or null if there isn't one.</summary>
    public static string? ComputeBackupHash(string wowDir)
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
    public void Rebuild(string wowDir, IReadOnlyList<PatchDefinition> catalog, ISet<string> enabledIds, IReadOnlyDictionary<string, double?> parameters)
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
            parameters.TryGetValue(patch.Id, out double? value);
            if (enabledIds.Contains(patch.Id))
            {
                ApplyPatch(ref image, patch, value);
            }
            else
            {
                ApplyPatchOff(ref image, patch, value);
            }
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
    private static void CleanupStaleTempFiles(string wowDir)
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
                IReadOnlyList<PatchStep>? defSteps = patch.BuildSteps?.Invoke(patch.Parameter.Default);
                if (defSteps is not null)
                {
                    bool anyRegionNonPristine = defSteps.Any(s =>
                        s.AcceptBefore is { Length: > 0 } fingerprints &&
                        !fingerprints.Any(fp => fp is not null && RegionEquals(image, checked((int)s.Offset), fp)));
                    if (anyRegionNonPristine)
                    {
                        applied.Add(patch.Id);
                    }
                }

                continue;
            }

            IReadOnlyList<PatchStep>? steps = patch.BuildSteps?.Invoke(null);
            if (steps is not null)
            {
                // A toggle patch whose every step carries a WriteOff is fully revertible regardless of
                // what state the exe is currently in - Rebuild can always reach either "on" (idempotent
                // Write) or "off" (explicit WriteOff) from here, so it's a legitimate pristine-backup
                // baseline either way and shouldn't block backup creation just because a community client
                // happens to ship it already toggled on. Only a patch with no revert path at all still
                // needs the strict "must currently be pristine" gate below.
                bool fullyRevertible = steps.Count > 0 && steps.All(s => s.WriteOff is not null);
                if (!fullyRevertible && steps.Count > 0 && steps.All(s => StepApplied(image, s)))
                {
                    applied.Add(patch.Id);
                }
            }
        }

        return applied;
    }

    private void ApplyPatch(ref byte[] image, PatchDefinition patch, double? parameter)
    {
        IReadOnlyList<PatchStep>? steps = patch.BuildSteps?.Invoke(parameter);
        IReadOnlyList<PatchStep.QuestLogPatchStruct>? questLogSteps = patch.BuildQuestLogSteps?.Invoke(parameter.HasValue ? (int)parameter.Value : null);

        if (steps is null && questLogSteps is null)
        {
            return;
        }
        if (parameter is not null)
        {
            _log.Info($"  Applying patch: {patch.Name} with value {parameter}");
        }
        else
        {
            _log.Info($"  Applying patch: {patch.Name}");
        }
        // Apply all the regular steps first, then the quest log steps. This order is important because some quest log steps may depend on the regular steps being applied first.
        if (steps is not null)
        {
            foreach (PatchStep step in steps)
            {
                ApplyStep(ref image, patch.Name, step, null, _log);
            }
        }
        if (questLogSteps is not null)
        {
            foreach (PatchStep.QuestLogPatchStruct questStep in questLogSteps)
            {
                ApplyStep(ref image, patch.Name, null, questStep, _log);
            }
        }
    }

    /// <summary>
    /// Reverts a disabled patch's steps that carry a <see cref="PatchStep.WriteOff"/> back to that
    /// value - needed because a community client's pristine backup can already contain the "on"
    /// bytes for a tweak it ships baked in, so merely skipping the write (the normal meaning of
    /// "disabled") would silently leave that baked-in value in place instead of actually turning it
    /// off. A step without a WriteOff is left untouched, same as before.
    /// </summary>
    private void ApplyPatchOff(ref byte[] image, PatchDefinition patch, double? parameter)
    {
        IReadOnlyList<PatchStep>? steps = patch.BuildSteps?.Invoke(parameter);
        if (steps is null || !steps.Any(s => s.WriteOff is not null))
        {
            return;
        }

        _log.Info($"  Reverting patch: {patch.Name}");
        foreach (PatchStep step in steps)
        {
            if (step.WriteOff is not null)
            {
                ApplyOffStep(ref image, step, patch.Name);
            }
        }
    }

    private static void ApplyStep(ref byte[] image, string patchName, PatchStep? step, PatchStep.QuestLogPatchStruct? questStep, Logger log)
    {
        if (step is not null)
        {
            if (step.Write is not { Length: > 0 } write)
            {
                throw new InvalidOperationException($"Patch '{patchName}': Write bytes must not be null or empty.");
            }

            if (step.Offset >= 0)
            {
                int offset = checked((int)step.Offset);
                if (offset + write.Length > image.Length)
                {
                    throw new InvalidOperationException($"Patch '{patchName}': offset 0x{offset:X} is beyond the end of the file.");
                }
                // Idempotent: already the target value, nothing to do.
                if (RegionEquals(image, offset, write))
                {
                    return;
                }
                byte[] localImage = image;
                if (step.AcceptBefore is { Length: > 0 } &&
                    !step.AcceptBefore.Any(expected => expected is not null && RegionEquals(localImage, offset, expected)))
                {
                    throw new InvalidOperationException($"Patch '{patchName}': unexpected bytes at 0x{offset:X}. The client is likely an " + "unsupported build; aborting so the executable is not corrupted.");
                }
                Buffer.BlockCopy(write, 0, image, offset, write.Length);
            }
            else
            {
                if (step.Find is null || step.Find.Length != write.Length)
                {
                    throw new InvalidOperationException($"Patch '{patchName}': malformed pattern step.");
                }
                int index = IndexOf(image, step.Find);
                if (index < 0)
                {
                    if (IndexOf(image, write) >= 0)
                    {
                        return; // already applied
                    }
                    throw new InvalidOperationException($"Patch '{patchName}': could not locate its target bytes (unexpected build or a " + "preceding patch changed this region).");
                }
                Buffer.BlockCopy(write, 0, image, index, write.Length);
            }
        }
        else if (questStep is not null)
        {
            if (questStep.Offset >= 0 && questStep.Length >= 0 && questStep.RVA >= 0 && questStep.ImageSize >= 0 && questStep.QuestRanges is not null)
            {
                try
                {
                    QuestLogPatcher.ValidatePeLayout(image, "VanillaWoW");

                    ushort sections = QuestLogPatcher.ByteHelpers.GetNumberOfSections(image);
                    if (sections != 6)
                    {
                        throw new InvalidDataException($"Executable expected 6 sections, found {sections}.");
                    }

                    int sectionTableOffset = QuestLogPatcher.ByteHelpers.GetSectionTableOffset(image);

                    // The seventh section header slot must already exist in the PE header area.
                    int slot7Offset = sectionTableOffset + (6 * 40);

                    if (!image.AsSpan(slot7Offset, 40).ToArray().All(b => b == 0))
                    {
                        throw new InvalidDataException("PE header has no free slot at the expected seventh-section header.");
                    }

                    if (image.Length != questStep.Offset)
                    {
                        throw new InvalidDataException($"Unexpected file size. Expected 0x{questStep.Offset:X}, found 0x{image.Length:X}.");
                    }

                    byte[][] questData = [.. questStep.QuestRanges.Select(r => r.RangeContent.ToArray())];

                    byte[] sectionHeader = [.. questStep.SectionHeaderBytes];
                    byte[] sectionPayload = [.. questStep.SectionPayloadBytes];

                    if (sectionHeader.Length != 40)
                    {
                        throw new InvalidDataException("Embedded .octoql section header is not 40 bytes.");
                    }

                    if (sectionPayload.Length != questStep.Length)
                    {
                        throw new InvalidDataException($"Embedded .octoql payload has unexpected size: 0x{sectionPayload.Length:X}.");
                    }

                    byte[] patched = (byte[])image.Clone();

                    // 1. Transplant the quest-log implementation.
                    for (int r = 0; r < questStep.QuestRanges.Count; r++)
                    {
                        var range = questStep.QuestRanges[r];
                        byte[] data = questData[r];

                        if (data.Length != range.RangeEnd - range.RangeStart)
                        {
                            log.Error($"Embedded quest range 0x{range.RangeStart:X}-0x{range.RangeEnd:X} has an unexpected size.");
                        }

                        if (range.RangeEnd > patched.Length)
                        {
                            log.Error("Quest patch range exceeds executable size.");
                        }

                        Buffer.BlockCopy(data, 0, patched, range.RangeStart, data.Length);
                    }

                    // 2. Add the .octoql seventh section header.
                    Buffer.BlockCopy(sectionHeader, 0, patched, sectionTableOffset + (6 * 40), sectionHeader.Length);

                    // 3. Update PE header: NumberOfSections 6 -> 7 and SizeOfImage.
                    int peOffset = QuestLogPatcher.ByteHelpers.ReadInt32(image, 0x3C);
                    QuestLogPatcher.ByteHelpers.WriteUInt16(patched, peOffset + 6, 7);

                    int optionalHeaderOffset = peOffset + 24;
                    QuestLogPatcher.ByteHelpers.WriteUInt32(patched, optionalHeaderOffset + 56, (uint)questStep.ImageSize);

                    // 4. Append the .octoql raw payload.
                    using (var ms = new MemoryStream(patched.Length + questStep.Length))
                    {
                        ms.Write(patched, 0, patched.Length);
                        ms.Write(sectionPayload, 0, sectionPayload.Length);
                        patched = ms.ToArray();
                    }

                    // Structural verification.
                    var isLayoutValid = QuestLogPatcher.ValidatePeLayout(patched, "patched executable");
                    var isQuestBytesValid = QuestLogPatcher.VerifyPatchedQuestBytes(patched, questData, questStep.QuestRanges);
                    var isSectionValid = QuestLogPatcher.VerifySection(patched, questStep);

                    if (!isLayoutValid && !isQuestBytesValid && !isSectionValid)
                    {
                        log.Error("Patched executable failed structural verification: PE layout, quest bytes, and section header are all invalid.");
                    }

                    // Update the image reference
                    image = patched;
                }
                catch (Exception ex)
                {
                    log.Error($"Error applying quest log patch: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Writes a step's <see cref="PatchStep.WriteOff"/> back, reverting whatever "on" value the
    /// pristine backup may already carry. Offset-mode only - every current WriteOff-bearing step is
    /// offset-based, so pattern-mode (Offset &lt; 0) is deliberately left unsupported here.
    /// </summary>
    private static void ApplyOffStep(ref byte[] image, PatchStep step, string patchName)
    {
        if (step.Offset < 0 || step.WriteOff is not { Length: > 0 } writeOff)
        {
            return;
        }

        int offset = checked((int)step.Offset);
        if (offset + writeOff.Length > image.Length)
        {
            throw new InvalidOperationException(
                $"Patch '{patchName}': revert offset 0x{offset:X} is beyond the end of the file.");
        }

        // Idempotent: already reverted, nothing to do.
        if (RegionEquals(image, offset, writeOff))
        {
            return;
        }

        // Safe to revert only from a state we recognize: either the patch's own "on" value (the
        // normal case - a community client's baked-in tweak) or one of its AcceptBefore fingerprints
        // (already pristine, so this is a genuine no-op once BlockCopy below runs). Anything else
        // means the exe isn't what we think it is; abort rather than write blind.
        bool recognizedOn = RegionEquals(image, offset, step.Write ?? [0x00]);
        byte[] localImage = image;
        bool recognizedBefore = step.AcceptBefore is { Length: > 0 } fingerprints &&
            fingerprints.Any(expected => expected is not null && RegionEquals(localImage, offset, expected));
        if (!recognizedOn && !recognizedBefore)
        {
            throw new InvalidOperationException(
                $"Patch '{patchName}': unexpected bytes at 0x{offset:X} while reverting. The client is " +
                "likely an unsupported build; aborting so the executable is not corrupted.");
        }

        Buffer.BlockCopy(writeOff, 0, image, offset, writeOff.Length);
    }

    private static bool StepApplied(byte[] image, PatchStep step)
    {
        if (step.Offset >= 0)
        {
            return RegionEquals(image, checked((int)step.Offset), step.Write ?? [0x00]);
        }

        return IndexOf(image, step.Write ?? [0x00]) >= 0 && (step.Find is null || IndexOf(image, step.Find!) < 0);
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

    private static List<PatchDefinition> OrderedForApply(IReadOnlyList<PatchDefinition> catalog)
        => [.. catalog.OrderBy(p => (int)p.Category)];

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
