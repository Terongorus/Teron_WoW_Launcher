using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

/// <summary>
/// The ordered catalog of available executable patches: Signature Removal first, then VanillaTweaks
/// (PatchService also sorts by <see cref="PatchCategory"/> defensively).
///
/// Every entry is our own implementation of a technique learned by studying how the corresponding
/// tool works — not copied code, and nothing external is executed. All offsets are for WoW 1.12.1
/// (build 5875); each step carries a pristine-original fingerprint in <see cref="PatchStep.AcceptBefore"/>
/// so an unexpected build aborts before anything is written. Value-based tweaks expose a
/// <see cref="PatchDefinition.Parameter"/> so the user can adjust them; toggles do not.
/// </summary>
public static class PatchCatalog
{
    public static IReadOnlyList<PatchDefinition> All { get; } = Build();

    private static IReadOnlyList<PatchDefinition> Build()
    {
        var list = new List<PatchDefinition>();

        // ================= 1. Signature Removal (applied first) =================
        list.Add(new PatchDefinition
        {
            Id = "signature-removal",
            Name = "Signature / integrity check removal",
            Description =
                "Disables the client's file-signature checks so custom MPQ patches and edited DBC " +
                "files load without warnings.",
            Category = PatchCategory.SignatureRemoval,
            DefaultEnabled = true,
            BuildSteps = _ => new List<PatchStep>
            {
                Off(0x2F113A, 0x5F), Off(0x2F113B, 0x5E), Off(0x2F1158, 0x01),
                Off(0x2F11A7, 0x01), Off(0x2F11F0, 0x5F), Off(0x2F11F1, 0x5E),
                Off(0x6AAAC, 0x9E, 0x1C), Off(0x6AAB0, 0x9E, 0x29), Off(0x6AAB4, 0x9E, 0x36),
                Off(0x901C8, 0x95, 0x5E), Off(0x901CC, 0x95, 0x6B), Off(0x901D0, 0x95, 0x78),
            },
        });

        // ================= 2. VanillaTweaks (applied second) =================

        // --- Toggle-only tweaks ---

        // Large Address Aware: PE Characteristics @0x126 |= 0x20 (0x010F -> 0x012F on 5875).
        list.Add(Toggle("laa", "Large Address Aware",
            "Lets the client use more than 2 GB of memory (helps with large custom content).",
            new List<PatchStep> { Raw(0x126, new byte[] { 0x2F, 0x01 }, new byte[] { 0x0F, 0x01 }) }));

        // Sound in background: @0x3A4869 0x14 -> 0x27.
        list.Add(Toggle("sound-in-background", "Sound in background",
            "Keeps game audio playing when the window loses focus.",
            new List<PatchStep> { Off(0x3A4869, 0x27, 0x14) }));

        // Quickloot reverse: two JZ->JNZ flips (0x74 -> 0x75). Hold Shift for manual loot.
        list.Add(Toggle("quickloot", "Auto-loot by default (hold Shift to manual-loot)",
            "Makes looting auto-loot by default; hold Shift for the manual loot window.",
            new List<PatchStep> { Off(0x0C1ECF, 0x75, 0x74), Off(0x0C2B25, 0x75, 0x74) }));

        // Camera rotation skip glitch fix (five code regions).
        list.Add(Toggle("camera-skip-fix", "Camera rotation skip glitch fix",
            "Fixes the camera occasionally snapping to a random direction when rotated.",
            new List<PatchStep>
            {
                Raw(0x02CCD0, new byte[]
                {
                    0x55, 0x8B, 0x05, 0x48, 0x4E, 0x88, 0x00, 0x8B, 0x0D, 0x44, 0x4E, 0x88, 0x00, 0xE9, 0x33, 0x90,
                    0x32, 0x00, 0x83, 0xC0, 0x32, 0x83, 0xC1, 0x32, 0x3B, 0x0D, 0xA8, 0xEB, 0xC4, 0x00, 0x7E, 0x03,
                    0x83, 0xE9, 0x01, 0x3B, 0x05, 0xAC, 0xEB, 0xC4, 0x00, 0x7E, 0x03, 0x83, 0xE8, 0x01, 0x83, 0xE9,
                    0x32, 0x83, 0xE8, 0x32, 0x89, 0x05, 0x48, 0x4E, 0x88, 0x00, 0x89, 0x0D, 0x44, 0x4E, 0x88, 0x00,
                    0x5D, 0xEB, 0x0D,
                }, new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10, 0x8D, 0x45 }),
                Raw(0x02D326, new byte[] { 0xE9, 0xB1, 0x8A, 0x32, 0x00 }, new byte[] { 0x8B, 0x45, 0xF0, 0x8B, 0x15 }),
                Raw(0x02D334, new byte[] { 0x8B, 0x35, 0x48, 0x4E, 0x88, 0x00 }, new byte[] { 0x8B, 0x35, 0x3C, 0x4E, 0x88, 0x00 }),
                Raw(0x355D15, new byte[]
                {
                    0x83, 0xF8, 0x32, 0x7D, 0x03, 0x83, 0xC0, 0x01, 0x83, 0xF9, 0x32, 0x7D, 0x03, 0x83, 0xC1, 0x01,
                    0xE9, 0xB8, 0x6F, 0xCD, 0xFF,
                }, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }),
                Raw(0x355DDC, new byte[]
                {
                    0x8D, 0x4D, 0xF0, 0x51, 0xFF, 0x35, 0x00, 0x4E, 0x88, 0x00, 0xFF, 0x15, 0x50, 0xF6, 0x7F, 0x00,
                    0x8B, 0x45, 0xF0, 0x8B, 0x15, 0x44, 0x4E, 0x88, 0x00, 0xE9, 0x35, 0x75, 0xCD, 0xFF,
                }, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }),
            }));

        // --- Adjustable tweaks ---

        list.Add(FloatTweak("fov", "Widescreen FoV", "Field of view (game default 90°).",
            0x4089B4, new byte[] { 0xDB, 0x0F, 0xC9, 0x3F }, min: 80, max: 126, def: 110, unit: "deg",
            convertToRaw: degrees => degrees * Math.PI / 180.0));

        list.Add(FloatTweak("farclip", "Render distance (farclip)",
            "Maximum terrain render distance (game default 777).",
            0x40FED8, new byte[] { 0x00, 0x40, 0x42, 0x44 }, min: 777, max: 10000, def: 10000, unit: "yds"));

        list.Add(FloatTweak("frilldistance", "Grass render distance",
            "Grass/detail-doodad render distance (game default 70).",
            0x467958, new byte[] { 0x00, 0x00, 0x8C, 0x42 }, min: 0, max: 500, def: 300, unit: "yds"));

        list.Add(FloatTweak("nameplate", "Nameplate distance",
            "Distance at which nameplates are visible (game default 20; client hard cap ~41 yds).",
            0x40C448, new byte[] { 0x00, 0x00, 0xA0, 0x41 }, min: 20, max: 41, def: 41, unit: "yds"));

        list.Add(FloatTweak("max-camera-distance", "Max camera distance limit",
            "Raises the CameraDistanceMax ceiling (game default 50). After enabling, set it in-game " +
            "with /console CameraDistanceMax.",
            0x4089A4, new byte[] { 0x00, 0x00, 0x48, 0x42 }, min: 15, max: 125, def: 100, unit: "yds", defaultEnabled: false));

        // Sound channels: ASCII digits (null-padded to 4 bytes) over "12\0\0" @0x435D38.
        list.Add(new PatchDefinition
        {
            Id = "soundchannels",
            Name = "Sound channels",
            Description = "Default software sound channel count (game default 12).",
            Category = PatchCategory.VanillaTweak,
            DefaultEnabled = true,
            Parameter = new PatchParameter { Min = 1, Max = 256, Default = 64, IsInteger = true },
            BuildSteps = v =>
            {
                int channels = Math.Clamp((int)Math.Round(v ?? 64), 1, 256);
                byte[] buffer = new byte[4];
                byte[] ascii = Encoding.ASCII.GetBytes(channels.ToString(CultureInfo.InvariantCulture));
                Array.Copy(ascii, buffer, Math.Min(ascii.Length, 4));
                return new List<PatchStep>
                {
                    new() { Offset = 0x435D38, Write = buffer, AcceptBefore = new[] { new byte[] { 0x31, 0x32, 0x00, 0x00 } } },
                };
            },
        });

        return list;
    }

    private static PatchDefinition Toggle(string id, string name, string description, List<PatchStep> steps)
        => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Category = PatchCategory.VanillaTweak,
            DefaultEnabled = true,
            BuildSteps = _ => steps,
        };

    /// <summary>
    /// A patch controlled by a single numeric UI value. That value is shown/edited as a whole
    /// number by default; when the client's own byte layout expects something else (e.g. FoV in
    /// radians while the UI shows degrees), <paramref name="convertToRaw"/> converts the UI value
    /// to the client's raw unit right before it's written — the UI/settings value itself always
    /// stays in the human-facing unit.
    /// </summary>
    private static PatchDefinition FloatTweak(
        string id, string name, string description, long offset, byte[] acceptBefore,
        double min, double max, double def, string? unit = null, bool defaultEnabled = true,
        bool isInteger = true, Func<double, double>? convertToRaw = null)
        => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Category = PatchCategory.VanillaTweak,
            DefaultEnabled = defaultEnabled,
            Parameter = new PatchParameter { Min = min, Max = max, Default = def, Unit = unit, IsInteger = isInteger },
            BuildSteps = v =>
            {
                double uiValue = v ?? def;
                double raw = convertToRaw is null ? uiValue : convertToRaw(uiValue);
                return new List<PatchStep>
                {
                    new()
                    {
                        Offset = offset,
                        Write = BitConverter.GetBytes((float)raw),
                        AcceptBefore = new[] { acceptBefore },
                    },
                };
            },
        };

    /// <summary>Single-byte fixed-offset write, optionally verified against pristine originals.</summary>
    private static PatchStep Off(long offset, byte value, params byte[] acceptOriginals)
    {
        byte[][]? accept = null;
        if (acceptOriginals.Length > 0)
        {
            accept = new byte[acceptOriginals.Length][];
            for (int i = 0; i < acceptOriginals.Length; i++)
            {
                accept[i] = new[] { acceptOriginals[i] };
            }
        }

        return new PatchStep { Offset = offset, Write = new[] { value }, AcceptBefore = accept };
    }

    /// <summary>Multi-byte fixed-offset write, verified against one pristine "before" sequence.</summary>
    private static PatchStep Raw(long offset, byte[] write, byte[]? acceptBefore = null)
        => new()
        {
            Offset = offset,
            Write = write,
            AcceptBefore = acceptBefore is null ? null : new[] { acceptBefore },
        };
}
