using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services.Patching;

/// <summary>
/// The per-<see cref="ClientCategory"/> catalog of available executable patches.
///
/// Every entry is our own implementation of a technique learned by studying how the corresponding
/// tool works — not copied code, and nothing external is executed. All offsets are for WoW 1.12.1
/// (build 5875); each step carries a pristine-original fingerprint in <see cref="PatchStep.AcceptBefore"/>
/// so an unexpected build aborts before anything is written. Value-based tweaks expose a
/// <see cref="PatchDefinition.Parameter"/> so the user can adjust them; toggles do not.
///
/// <para>
/// TurtleWoW and OctoWoW were both diffed directly, byte-for-byte, against these same offsets on
/// real client executables (Turtle's own install, and a fresh, never-configured OctoWoW download).
/// Both are byte-identical to vanilla 5875 in file layout everywhere checked, and both already carry
/// most of the "VanillaTweak" set applied natively — <c>laa</c>, <c>sound-in-background</c>,
/// <c>quickloot</c>, <c>camera-skip-fix</c>, <c>fov</c> (110°) and <c>nameplate</c> (41 yds) are all
/// already at their patched values out of the box. All six are still offered as real, togglable
/// entries here: each carries a <see cref="PatchStep.WriteOff"/> (the true vanilla pristine bytes,
/// the same ones already used as this patch's own <see cref="PatchStep.AcceptBefore"/> fingerprint),
/// so unchecking one on a community client actively reverts that region to Blizzard's original bytes
/// during rebuild rather than merely skipping the write (which would silently leave the client's own
/// baked-in value in place). Since a fresh directory's own baked-in state would otherwise look like
/// an unrequested revert the moment patches first sync, these six default to checked (see
/// <see cref="PatchDefinition.DefaultEnabled"/>) on TurtleWoW/OctoWoW specifically, once, the first
/// time that directory's patches are configured.
/// </para>
///
/// <para>
/// <c>signature-removal</c>'s first 6 steps (offsets 0x2F113A/2F113B/2F1158/2F11A7/2F11F0/2F11F1)
/// turned out to be a no-op on genuine vanilla — reading them from a real pristine vanilla backup
/// showed the pristine value there already equals this patch's own <c>Write</c> target. Turtle and
/// OctoWoW have both independently moved away from that same value to something else entirely
/// (their own loader/anti-tamper code occupying that address, unrelated to Blizzard's original
/// check), so those 6 steps are simply left out of the community-client variant rather than being
/// attempted and refused. The other 6 steps of that same patch (0x6AAAC/6AAB0/6AAB4/901C8/901CC/
/// 901D0) are confirmed still pristine on both clients and stay in both variants.
/// </para>
/// </summary>
public static class PatchCatalog
{
    // --- Pristine vanilla "before" fingerprints, shared between the vanilla catalog and the
    // community-client variants (which layer their own additional accepted starting values on top
    // of these, since a community client that hasn't touched a given region is still just as
    // pristine there as real vanilla is). Must be declared before All/CommunityTurtleWoW/
    // CommunityOctoWoW below: C# runs static field initializers in declaration order, and
    // BuildVanilla()/BuildCommunityPatched() capture these arrays by value at that time - if they
    // ran first, they'd capture still-null fields and bake a null fingerprint into the catalog
    // (crashes later in PatchService.RegionEquals). ---
    private static readonly byte[] VanillaFovBefore = { 0xDB, 0x0F, 0xC9, 0x3F };
    private static readonly byte[] VanillaFarclipBefore = { 0x00, 0x40, 0x42, 0x44 };
    private static readonly byte[] VanillaFrilldistanceBefore = { 0x00, 0x00, 0x8C, 0x42 };
    private static readonly byte[] VanillaNameplateBefore = { 0x00, 0x00, 0xA0, 0x41 };
    private static readonly byte[] VanillaMaxCameraBefore = { 0x00, 0x00, 0x48, 0x42 };

    // Confirmed by reading each client's own shipped WoW.exe directly at these offsets - not the
    // vanilla pristine value, but a legitimate untouched-by-the-user starting point for that client.
    private static readonly byte[] TurtleFarclipBefore = { 0x00, 0x80, 0xBB, 0x44 }; // 1500 yds
    private static readonly byte[] TurtleFrilldistanceBefore = { 0x00, 0x00, 0x96, 0x43 }; // 300 yds
    private static readonly byte[] OctoWowFarclipBefore = { 0x00, 0x80, 0x3B, 0x45 }; // 3000 yds

    // Read directly from a real TurtleWoW WoW.exe (2026-08-24) that otherwise matched every other
    // TurtleWoW fingerprint exactly (fov/farclip/frilldistance/nameplate/signature-removal) - this
    // build ships CameraDistanceMax already raised to 100, contradicting the class doc comment's
    // original "max-camera-distance is untouched on both clients" claim. Accepted as a genuine
    // TurtleWoW baseline rather than assumed tampering, matching the farclip/frilldistance pattern.
    private static readonly byte[] TurtleMaxCameraBefore = { 0x00, 0x00, 0xC8, 0x42 }; // 100 yds

    // Confirmed identical on both TurtleWoW and OctoWoW (read directly from each client's own
    // WoW.exe) - both ship with FoV/nameplate already raised to their patched values, so both share
    // one constant rather than needing a per-client variant like farclip/frilldistance above.
    private static readonly byte[] CommunityFovBefore = { 0x0B, 0xBE, 0xF5, 0x3F }; // 110°
    private static readonly byte[] CommunityNameplateBefore = { 0x00, 0x00, 0x24, 0x42 }; // 41 yds

    /// <summary>The vanilla catalog. Kept as the default for any caller that hasn't been made
    /// category-aware yet - prefer <see cref="For"/> for anything directory-scoped.</summary>
    public static IReadOnlyList<PatchDefinition> All { get; } = BuildVanilla();

    /// <summary>
    /// <paramref name="profileId"/> is the directory's assigned <see cref="ClientProfile.Id"/> (or
    /// null for an unassigned directory, which callers should generally have already refused to reach
    /// this point for - see MainWindow's no-profile-assigned dialog). Only consulted for
    /// <see cref="ClientCategory.VanillaPlus"/>, to layer a known built-in seed's own byte-verified
    /// starting values (see <see cref="ClientProfile"/>'s seed id constants) on top of the generic
    /// Vanilla+ baseline - a custom, non-seed profile id simply matches none of them and gets the
    /// generic baseline only.
    /// </summary>
    public static IReadOnlyList<PatchDefinition> For(ClientCategory category, string? profileId) => category switch
    {
        ClientCategory.VanillaPlus => BuildVanillaPlus(profileId),
        _ => BuildVanilla(),
    };

    private static IReadOnlyList<PatchDefinition> BuildVanilla()
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
            MandatoryForMpq = true,
            BuildSteps = _ => new List<PatchStep>
            {
                // These 6 turned out to be a no-op on genuine vanilla (its pristine value already
                // equals the Write target below) - now fingerprinted with that same value as
                // AcceptBefore so an unexpected build (e.g. a community client that has replaced
                // this code region with its own logic) safely refuses here too, instead of writing
                // blind. See the class doc comment.
                Off(0x2F113A, 0x5F, 0x5F), Off(0x2F113B, 0x5E, 0x5E), Off(0x2F1158, 0x01, 0x01),
                Off(0x2F11A7, 0x01, 0x01), Off(0x2F11F0, 0x5F, 0x5F), Off(0x2F11F1, 0x5E, 0x5E),
                Off(0x6AAAC, 0x9E, 0x1C), Off(0x6AAB0, 0x9E, 0x29), Off(0x6AAB4, 0x9E, 0x36),
                Off(0x901C8, 0x95, 0x5E), Off(0x901CC, 0x95, 0x6B), Off(0x901D0, 0x95, 0x78),
            },
        });

        // ================= 2. VanillaTweaks (applied second) =================

        // --- Toggle-only tweaks ---

        list.Add(Toggle("laa", "Large Address Aware",
            "Lets the client use more than 2 GB of memory (helps with large custom content).",
            LaaSteps(), mandatoryForMpq: true));

        list.Add(Toggle("sound-in-background", "Sound in background",
            "Keeps game audio playing when the window loses focus.",
            SoundInBackgroundSteps()));

        list.Add(Toggle("quickloot", "Auto-loot by default (hold Shift to manual-loot)",
            "Makes looting auto-loot by default; hold Shift for the manual loot window.",
            QuicklootSteps()));

        list.Add(Toggle("camera-skip-fix", "Camera rotation skip glitch fix",
            "Fixes the camera occasionally snapping to a random direction when rotated.",
            CameraSkipFixSteps()));

        // --- Adjustable tweaks ---

        list.Add(FloatTweak("fov", "Widescreen FoV", "Field of view (game default 90°).",
            0x4089B4, new[] { VanillaFovBefore }, min: 80, max: 126, def: 110, unit: "deg",
            convertToRaw: degrees => degrees * Math.PI / 180.0));

        list.Add(FloatTweak("farclip", "Render distance (farclip)",
            "Maximum terrain render distance (game default 777).",
            0x40FED8, new[] { VanillaFarclipBefore }, min: 777, max: 10000, def: 10000, unit: "yds"));

        list.Add(FloatTweak("frilldistance", "Grass render distance",
            "Grass/detail-doodad render distance (game default 70).",
            0x467958, new[] { VanillaFrilldistanceBefore }, min: 0, max: 500, def: 300, unit: "yds"));

        list.Add(FloatTweak("nameplate", "Nameplate distance",
            "Distance at which nameplates are visible (game default 20; client hard cap ~41 yds).",
            0x40C448, new[] { VanillaNameplateBefore }, min: 20, max: 41, def: 41, unit: "yds"));

        list.Add(FloatTweak("max-camera-distance", "Max camera distance limit",
            "Raises the CameraDistanceMax ceiling (game default 50). After enabling, set it in-game " +
            "with /console CameraDistanceMax.",
            0x4089A4, new[] { VanillaMaxCameraBefore }, min: 15, max: 125, def: 100, unit: "yds"));

        list.Add(SoundChannels());

        return list;
    }

    /// <summary>
    /// The shared Vanilla+ catalog: only the patches confirmed genuinely safe to apply on both known
    /// seed clients (see the class doc comment) - <c>soundchannels</c> and <c>max-camera-distance</c>
    /// are untouched on both, <c>signature-removal</c> keeps only its still-pristine 6 steps, and
    /// <c>farclip</c>/<c>frilldistance</c> accept a known seed's own confirmed starting value in
    /// addition to vanilla's, when <paramref name="profileId"/> matches one (<see cref="ClientProfile.OctoWowSeedId"/>/
    /// <see cref="ClientProfile.TurtleWowSeedId"/>) - any other profile id (including null, or a
    /// user-created custom Vanilla+ profile) gets the generic baseline only: vanilla's own default is
    /// the sole accepted starting point for those two, so an unknown server that's genuinely untouched
    /// there still works, and one that already changed them safely refuses until pristine.
    /// </summary>
    private static IReadOnlyList<PatchDefinition> BuildVanillaPlus(string? profileId)
    {
        var list = new List<PatchDefinition>();

        list.Add(new PatchDefinition
        {
            Id = "signature-removal",
            Name = "Signature / integrity check removal",
            Description =
                "Disables the client's file-signature checks so custom MPQ patches and edited DBC " +
                "files load without warnings.",
            Category = PatchCategory.SignatureRemoval,
            MandatoryForMpq = true,
            BuildSteps = _ => new List<PatchStep>
            {
                Off(0x6AAAC, 0x9E, 0x1C), Off(0x6AAB0, 0x9E, 0x29), Off(0x6AAB4, 0x9E, 0x36),
                Off(0x901C8, 0x95, 0x5E), Off(0x901CC, 0x95, 0x6B), Off(0x901D0, 0x95, 0x78),
            },
        });

        // Same relative order as BuildVanilla's optional section (toggles, then adjustable tweaks,
        // then sound channels) so the Tweaks tab lists patches in the same sequence regardless of
        // client type - only the two default-enabled/reversibility differences noted inline vary.
        list.Add(Toggle("laa", "Large Address Aware",
            "Lets the client use more than 2 GB of memory (helps with large custom content).",
            LaaSteps(), mandatoryForMpq: true, defaultEnabled: true));

        // These four ship already baked "on" in both clients' factory exe - unlike the parameter
        // tweaks below, unchecking one must actively revert it (see PatchStep.WriteOff), so default
        // to checked (once - see PatchDefinition.DefaultEnabled) rather than looking like an
        // unrequested revert of the client's own defaults the first time patches sync.
        list.Add(Toggle("sound-in-background", "Sound in background",
            "Keeps game audio playing when the window loses focus.",
            SoundInBackgroundSteps(), defaultEnabled: true));

        list.Add(Toggle("quickloot", "Auto-loot by default (hold Shift to manual-loot)",
            "Makes looting auto-loot by default; hold Shift for the manual loot window.",
            QuicklootSteps(), defaultEnabled: true));

        list.Add(Toggle("camera-skip-fix", "Camera rotation skip glitch fix",
            "Fixes the camera occasionally snapping to a random direction when rotated.",
            CameraSkipFixSteps(), defaultEnabled: true));

        // Confirmed identical (110°/41 yds) on both TurtleWoW and OctoWoW - see CommunityFovBefore/
        // CommunityNameplateBefore. Disabling either here is a no-op like every other parameter tweak
        // (it just leaves whatever the pristine backup already has, e.g. the client's own baked-in
        // 110°/41 yds) - true reversion only happens by dragging the slider back down and re-syncing,
        // exactly like farclip/frilldistance below already work.
        list.Add(FloatTweak("fov", "Widescreen FoV",
            "Field of view (game default 90°; this client ships at 110°).",
            0x4089B4, new[] { VanillaFovBefore, CommunityFovBefore }, min: 80, max: 126, def: 110, unit: "deg",
            convertToRaw: degrees => degrees * Math.PI / 180.0));

        List<byte[]> farclipBefore = new() { VanillaFarclipBefore };
        if (profileId == ClientProfile.TurtleWowSeedId)
        {
            farclipBefore.Add(TurtleFarclipBefore);
        }
        else if (profileId == ClientProfile.OctoWowSeedId)
        {
            farclipBefore.Add(OctoWowFarclipBefore);
        }

        list.Add(FloatTweak("farclip", "Render distance (farclip)",
            "Maximum terrain render distance (game default 777).",
            0x40FED8, farclipBefore.ToArray(), min: 777, max: 10000, def: 10000, unit: "yds"));

        List<byte[]> frilldistanceBefore = new() { VanillaFrilldistanceBefore };
        if (profileId == ClientProfile.TurtleWowSeedId)
        {
            frilldistanceBefore.Add(TurtleFrilldistanceBefore);
        }

        list.Add(FloatTweak("frilldistance", "Grass render distance",
            "Grass/detail-doodad render distance (game default 70).",
            0x467958, frilldistanceBefore.ToArray(), min: 0, max: 500, def: 300, unit: "yds"));

        list.Add(FloatTweak("nameplate", "Nameplate distance",
            "Distance at which nameplates are visible (game default 20; client hard cap ~41 yds; " +
            "this client ships at 41).",
            0x40C448, new[] { VanillaNameplateBefore, CommunityNameplateBefore }, min: 20, max: 41, def: 41, unit: "yds"));

        List<byte[]> maxCameraBefore = new() { VanillaMaxCameraBefore };
        if (profileId == ClientProfile.TurtleWowSeedId)
        {
            maxCameraBefore.Add(TurtleMaxCameraBefore);
        }

        list.Add(FloatTweak("max-camera-distance", "Max camera distance limit",
            "Raises the CameraDistanceMax ceiling (game default 50). After enabling, set it in-game " +
            "with /console CameraDistanceMax.",
            0x4089A4, maxCameraBefore.ToArray(), min: 15, max: 125, def: 100, unit: "yds"));

        list.Add(SoundChannels());

        return list;
    }

    // --- Shared toggle step-builders, reused by both BuildVanilla and BuildCommunityPatched so the
    // exact same offsets/bytes back both catalogs' entries for these four (see the class doc comment
    // for why bidirectional reversibility is safe here: each step's WriteOff, populated automatically
    // by Off()/Raw() below, is the same vanilla-pristine value already used as its own AcceptBefore
    // fingerprint). ---

    // Large Address Aware: PE Characteristics @0x126 |= 0x20 (0x010F -> 0x012F on 5875).
    private static List<PatchStep> LaaSteps() => new()
    {
        Raw(0x126, new byte[] { 0x2F, 0x01 }, new byte[] { 0x0F, 0x01 }),
    };

    // Sound in background: @0x3A4869 0x14 -> 0x27.
    private static List<PatchStep> SoundInBackgroundSteps() => new()
    {
        Off(0x3A4869, 0x27, 0x14),
    };

    // Quickloot reverse: two JZ->JNZ flips (0x74 -> 0x75). Hold Shift for manual loot.
    private static List<PatchStep> QuicklootSteps() => new()
    {
        Off(0x0C1ECF, 0x75, 0x74), Off(0x0C2B25, 0x75, 0x74),
    };

    // Camera rotation skip glitch fix (four code regions).
    private static List<PatchStep> CameraSkipFixSteps() => new()
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
    };

    // Sound channels: ASCII digits (null-padded to 4 bytes) over "12\0\0" @0x435D38 - identical
    // between vanilla and both community clients (confirmed genuinely untouched on all three).
    private static PatchDefinition SoundChannels() => new()
    {
        Id = "soundchannels",
        Name = "Sound channels",
        Description = "Default software sound channel count (game default 12).",
        Category = PatchCategory.VanillaTweak,
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
    };

    private static PatchDefinition Toggle(
        string id, string name, string description, List<PatchStep> steps, bool mandatoryForMpq = false,
        bool defaultEnabled = false)
        => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Category = PatchCategory.VanillaTweak,
            MandatoryForMpq = mandatoryForMpq,
            DefaultEnabled = defaultEnabled,
            BuildSteps = _ => steps,
        };

    /// <summary>
    /// A patch controlled by a single numeric UI value. That value is shown/edited as a whole
    /// number by default; when the client's own byte layout expects something else (e.g. FoV in
    /// radians while the UI shows degrees), <paramref name="convertToRaw"/> converts the UI value
    /// to the client's raw unit right before it's written — the UI/settings value itself always
    /// stays in the human-facing unit. <paramref name="acceptBeforeOptions"/> lists every byte
    /// sequence accepted as a legitimate untouched starting point at this offset - normally just
    /// vanilla's own pristine value, but a community client catalog can list its own confirmed
    /// starting value here too alongside vanilla's.
    /// </summary>
    private static PatchDefinition FloatTweak(
        string id, string name, string description, long offset, byte[][] acceptBeforeOptions,
        double min, double max, double def, string? unit = null,
        bool isInteger = true, Func<double, double>? convertToRaw = null)
        => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Category = PatchCategory.VanillaTweak,
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
                        AcceptBefore = acceptBeforeOptions,
                    },
                };
            },
        };

    /// <summary>
    /// Single-byte fixed-offset write, optionally verified against pristine originals. The first
    /// accepted original also becomes this step's <see cref="PatchStep.WriteOff"/> - the same value
    /// used to prove the region was pristine doubles as what to restore when reverting.
    /// </summary>
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

        return new PatchStep
        {
            Offset = offset,
            Write = new[] { value },
            AcceptBefore = accept,
            WriteOff = acceptOriginals.Length > 0 ? new[] { acceptOriginals[0] } : null,
        };
    }

    /// <summary>
    /// Multi-byte fixed-offset write, verified against one pristine "before" sequence, which also
    /// becomes this step's <see cref="PatchStep.WriteOff"/> (see <see cref="Off"/>).
    /// </summary>
    private static PatchStep Raw(long offset, byte[] write, byte[]? acceptBefore = null)
        => new()
        {
            Offset = offset,
            Write = write,
            AcceptBefore = acceptBefore is null ? null : new[] { acceptBefore },
            WriteOff = acceptBefore,
        };
}
