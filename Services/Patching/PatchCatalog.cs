using System;
using System.Collections.Generic;
using System.Drawing.Text;
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
    private static readonly byte[] VanillaFovBefore = [0xDB, 0x0F, 0xC9, 0x3F];
    private static readonly byte[] VanillaFarclipBefore = [0x00, 0x40, 0x42, 0x44];
    private static readonly byte[] VanillaFrilldistanceBefore = [0x00, 0x00, 0x8C, 0x42];
    private static readonly byte[] VanillaNameplateBefore = [0x00, 0x00, 0xA0, 0x41];
    private static readonly byte[] VanillaMaxCameraBefore = [0x00, 0x00, 0x48, 0x42];

    // Confirmed by reading each client's own shipped WoW.exe directly at these offsets - not the
    // vanilla pristine value, but a legitimate untouched-by-the-user starting point for that client.
    private static readonly byte[] TurtleFarclipBefore = [0x00, 0x80, 0xBB, 0x44]; // 1500 yds
    private static readonly byte[] TurtleFrilldistanceBefore = [0x00, 0x00, 0x96, 0x43]; // 300 yds
    private static readonly byte[] OctoWowFarclipBefore = [0x00, 0x80, 0x3B, 0x45]; // 3000 yds

    // Read directly from a real TurtleWoW WoW.exe (2026-08-24) that otherwise matched every other
    // TurtleWoW fingerprint exactly (fov/farclip/frilldistance/nameplate/signature-removal) - this
    // build ships CameraDistanceMax already raised to 100, contradicting the class doc comment's
    // original "max-camera-distance is untouched on both clients" claim. Accepted as a genuine
    // TurtleWoW baseline rather than assumed tampering, matching the farclip/frilldistance pattern.
    private static readonly byte[] TurtleMaxCameraBefore = [0x00, 0x00, 0xC8, 0x42]; // 100 yds

    // Confirmed identical on both TurtleWoW and OctoWoW (read directly from each client's own
    // WoW.exe) - both ship with FoV/nameplate already raised to their patched values, so both share
    // one constant rather than needing a per-client variant like farclip/frilldistance above.
    private static readonly byte[] CommunityFovBefore = [0x0B, 0xBE, 0xF5, 0x3F]; // 110°
    private static readonly byte[] CommunityNameplateBefore = [0x00, 0x00, 0x24, 0x42]; // 41 yds

    private static readonly int OctoWoWQuestLogSectionOffset = 0x4AE000;
    private static readonly int OctoWoWQuestLogSectionLength = 0x5000;
    private static readonly int OctoWoWQuestLogSectionRVA = 0x926000;
    private static readonly int OctoWoWQuestLogSectionImageSize = 0x92B000;
    private static readonly List<QuestLogPatcher.QuestRangeStruct> QuestRanges =
    [
        new QuestLogPatcher.QuestRangeStruct(
            0xDE000,
            0xE1700,
            "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"),
        new QuestLogPatcher.QuestRangeStruct(
            0x1DDD00,
            0x1DDE20,
            "CFBRuowBhgC5EAAAAOhOp+j/hcB0XYuIOB0AAIHGWPv//8HuAzvxcgYzwDPJ6w2LkDwdAACLBPKLTPIEi1UQV4s6O/hfdQeLUgQ70XQHUVDoy3br/4P+J3IFg/4+dgqD/j9yD4P+RHcKuVsBAADo3WASALgBAAAAXl3CEACQkJBTi9yD7AiD5PiDxARVi2sEiWwkBIvsg+wQi0MMVldoYw0AAIlN/ItLCFBRuowBhgC5EAAAAOimpuj/i/CF9g+E2QAAAItWCIs6i0IEiUX06Hyn6P87+HUKOVX0dQXojtIAAItN/IPB2Lirqqqq9+HB6gOF0nwXg/oZdxKLhmgOAADoRoJ0AJCQiU386wfHRfwAAAAAi1X8iwKFwIt7EHRi")
    ];

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

    private static List<PatchDefinition> BuildVanilla()
    {
        var list = new List<PatchDefinition>
        {
            // ================= 1. Signature Removal (applied first) =================
            new() {
                Id = "signature-removal",
                Name = "Signature / integrity check removal",
                Description =
                "Disables the client's file-signature checks so custom MPQ patches and edited DBC " +
                "files load without warnings.",
                Category = PatchCategory.SignatureRemoval,
                MandatoryForMpq = true,
                BuildSteps = _ =>
                [
                    // These 6 turned out to be a no-op on genuine vanilla (its pristine value already
                    // equals the Write target below) - now fingerprinted with that same value as
                    // AcceptBefore so an unexpected build (e.g. a community client that has replaced
                    // this code region with its own logic) safely refuses here too, instead of writing
                    // blind. See the class doc comment.
                    Off(0x2F113A, 0x5F, 0x5F), Off(0x2F113B, 0x5E, 0x5E), Off(0x2F1158, 0x01, 0x01),
                    Off(0x2F11A7, 0x01, 0x01), Off(0x2F11F0, 0x5F, 0x5F), Off(0x2F11F1, 0x5E, 0x5E),
                    Off(0x6AAAC, 0x9E, 0x1C), Off(0x6AAB0, 0x9E, 0x29), Off(0x6AAB4, 0x9E, 0x36),
                    Off(0x901C8, 0x95, 0x5E), Off(0x901CC, 0x95, 0x6B), Off(0x901D0, 0x95, 0x78),
                ],
                BuildQuestLogSteps = null
            },

            // ================= 2. VanillaTweaks (applied second) =================

            // --- Toggle-only tweaks ---

            Toggle("laa", "Large Address Aware",
            "Lets the client use more than 2 GB of memory (helps with large custom content).",
            LaaSteps(), mandatoryForMpq: true),
            Toggle("sound-in-background", "Sound in background",
            "Keeps game audio playing when the window loses focus.",
            SoundInBackgroundSteps()),
            Toggle("quickloot", "Auto-loot by default (hold Shift to manual-loot)",
            "Makes looting auto-loot by default; hold Shift for the manual loot window.",
            QuicklootSteps()),
            Toggle("camera-skip-fix", "Camera rotation skip glitch fix",
            "Fixes the camera occasionally snapping to a random direction when rotated.",
            CameraSkipFixSteps()),

            // --- Adjustable tweaks ---

            FloatTweak("fov", "Widescreen FoV", "Field of view (game default 90°).",
            0x4089B4, [VanillaFovBefore], min: 80, max: 126, def: 110, unit: "deg",
            convertToRaw: degrees => degrees * Math.PI / 180.0),
            FloatTweak("farclip", "Render distance (farclip)",
            "Maximum terrain render distance (game default 777).",
            0x40FED8, [VanillaFarclipBefore], min: 777, max: 10000, def: 10000, unit: "yds"),
            FloatTweak("frilldistance", "Grass render distance",
            "Grass/detail-doodad render distance (game default 70).",
            0x467958, [VanillaFrilldistanceBefore], min: 0, max: 500, def: 300, unit: "yds"),
            FloatTweak("nameplate", "Nameplate distance",
            "Distance at which nameplates are visible (game default 20; client hard cap ~41 yds).",
            0x40C448, [VanillaNameplateBefore], min: 20, max: 41, def: 41, unit: "yds"),
            FloatTweak("max-camera-distance", "Max camera distance limit",
            "Raises the CameraDistanceMax ceiling (game default 50). After enabling, set it in-game " +
            "with /console CameraDistanceMax.",
            0x4089A4, [VanillaMaxCameraBefore], min: 15, max: 125, def: 100, unit: "yds"),
            SoundChannels()
        };

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
    private static List<PatchDefinition> BuildVanillaPlus(string? profileId)
    {
        var list = new List<PatchDefinition>
        {
            new() {
                Id = "signature-removal",
                Name = "Signature / integrity check removal",
                Description =
                "Disables the client's file-signature checks so custom MPQ patches and edited DBC " +
                "files load without warnings.",
                Category = PatchCategory.SignatureRemoval,
                MandatoryForMpq = true,
                BuildSteps = _ =>
                [
                    Off(0x6AAAC, 0x9E, 0x1C), Off(0x6AAB0, 0x9E, 0x29), Off(0x6AAB4, 0x9E, 0x36),
                    Off(0x901C8, 0x95, 0x5E), Off(0x901CC, 0x95, 0x6B), Off(0x901D0, 0x95, 0x78),
                ],
                BuildQuestLogSteps = null
            },

            // Same relative order as BuildVanilla's optional section (toggles, then adjustable tweaks,
            // then sound channels) so the Tweaks tab lists patches in the same sequence regardless of
            // client type - only the two default-enabled/reversibility differences noted inline vary.
            Toggle("laa", "Large Address Aware",
            "Lets the client use more than 2 GB of memory (helps with large custom content).",
            LaaSteps(), mandatoryForMpq: true, defaultEnabled: true),

            // These four ship already baked "on" in both clients' factory exe - unlike the parameter
            // tweaks below, unchecking one must actively revert it (see PatchStep.WriteOff), so default
            // to checked (once - see PatchDefinition.DefaultEnabled) rather than looking like an
            // unrequested revert of the client's own defaults the first time patches sync.
            Toggle("sound-in-background", "Sound in background",
            "Keeps game audio playing when the window loses focus.",
            SoundInBackgroundSteps(), defaultEnabled: true),
            Toggle("quickloot", "Auto-loot by default (hold Shift to manual-loot)",
            "Makes looting auto-loot by default; hold Shift for the manual loot window.",
            QuicklootSteps(), defaultEnabled: true),
            Toggle("camera-skip-fix", "Camera rotation skip glitch fix",
            "Fixes the camera occasionally snapping to a random direction when rotated.",
            CameraSkipFixSteps(), defaultEnabled: true),

            // Confirmed identical (110°/41 yds) on both TurtleWoW and OctoWoW - see CommunityFovBefore/
            // CommunityNameplateBefore. Disabling either here is a no-op like every other parameter tweak
            // (it just leaves whatever the pristine backup already has, e.g. the client's own baked-in
            // 110°/41 yds) - true reversion only happens by dragging the slider back down and re-syncing,
            // exactly like farclip/frilldistance below already work.
            FloatTweak("fov", "Widescreen FoV",
            "Field of view (game default 90°; this client ships at 110°).",
            0x4089B4, [VanillaFovBefore, CommunityFovBefore], min: 80, max: 126, def: 110, unit: "deg",
            convertToRaw: degrees => degrees * Math.PI / 180.0)
        };

        List<byte[]> farclipBefore = [VanillaFarclipBefore];
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
            0x40FED8, [.. farclipBefore], min: 777, max: 10000, def: 10000, unit: "yds"));

        List<byte[]> frilldistanceBefore = [VanillaFrilldistanceBefore];
        if (profileId == ClientProfile.TurtleWowSeedId)
        {
            frilldistanceBefore.Add(TurtleFrilldistanceBefore);
        }

        list.Add(FloatTweak("frilldistance", "Grass render distance",
            "Grass/detail-doodad render distance (game default 70).",
            0x467958, [.. frilldistanceBefore], min: 0, max: 500, def: 300, unit: "yds"));

        list.Add(FloatTweak("nameplate", "Nameplate distance",
            "Distance at which nameplates are visible (game default 20; client hard cap ~41 yds; " +
            "this client ships at 41).",
            0x40C448, [VanillaNameplateBefore, CommunityNameplateBefore], min: 20, max: 41, def: 41, unit: "yds"));

        List<byte[]> maxCameraBefore = [VanillaMaxCameraBefore];
        if (profileId == ClientProfile.TurtleWowSeedId)
        {
            maxCameraBefore.Add(TurtleMaxCameraBefore);
        }

        list.Add(FloatTweak("max-camera-distance", "Max camera distance limit",
            "Raises the CameraDistanceMax ceiling (game default 50). After enabling, set it in-game " +
            "with /console CameraDistanceMax.",
            0x4089A4, [.. maxCameraBefore], min: 15, max: 125, def: 100, unit: "yds"));

        list.Add(SoundChannels());

        list.Add(QuestTweak("quest-log-patch", "Quest Log Patch", "Increases the amount of quests displayed in the quest log. Depends on the amount of quests returned by the backend of the realm that you're playing on.", OctoWoWQuestLogSectionOffset, OctoWoWQuestLogSectionLength, OctoWoWQuestLogSectionRVA, OctoWoWQuestLogSectionImageSize, QuestRanges, 0, 40, 20, true));

        return list;
    }

    // --- Shared toggle step-builders, reused by both BuildVanilla and BuildCommunityPatched so the
    // exact same offsets/bytes back both catalogs' entries for these four (see the class doc comment
    // for why bidirectional reversibility is safe here: each step's WriteOff, populated automatically
    // by Off()/Raw() below, is the same vanilla-pristine value already used as its own AcceptBefore
    // fingerprint). ---

    // Large Address Aware: PE Characteristics @0x126 |= 0x20 (0x010F -> 0x012F on 5875).
    private static List<PatchStep> LaaSteps() =>
    [
        Raw(0x126, [0x2F, 0x01], [0x0F, 0x01]),
    ];

    // Sound in background: @0x3A4869 0x14 -> 0x27.
    private static List<PatchStep> SoundInBackgroundSteps() =>
    [
        Off(0x3A4869, 0x27, 0x14),
    ];

    // Quickloot reverse: two JZ->JNZ flips (0x74 -> 0x75). Hold Shift for manual loot.
    private static List<PatchStep> QuicklootSteps() =>
    [
        Off(0x0C1ECF, 0x75, 0x74), Off(0x0C2B25, 0x75, 0x74),
    ];

    // Camera rotation skip glitch fix (four code regions).
    private static List<PatchStep> CameraSkipFixSteps() =>
    [
        Raw(0x02CCD0,
        [
            0x55, 0x8B, 0x05, 0x48, 0x4E, 0x88, 0x00, 0x8B, 0x0D, 0x44, 0x4E, 0x88, 0x00, 0xE9, 0x33, 0x90,
            0x32, 0x00, 0x83, 0xC0, 0x32, 0x83, 0xC1, 0x32, 0x3B, 0x0D, 0xA8, 0xEB, 0xC4, 0x00, 0x7E, 0x03,
            0x83, 0xE9, 0x01, 0x3B, 0x05, 0xAC, 0xEB, 0xC4, 0x00, 0x7E, 0x03, 0x83, 0xE8, 0x01, 0x83, 0xE9,
            0x32, 0x83, 0xE8, 0x32, 0x89, 0x05, 0x48, 0x4E, 0x88, 0x00, 0x89, 0x0D, 0x44, 0x4E, 0x88, 0x00,
            0x5D, 0xEB, 0x0D,
        ], [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10, 0x8D, 0x45]),
        Raw(0x02D326, [0xE9, 0xB1, 0x8A, 0x32, 0x00], [0x8B, 0x45, 0xF0, 0x8B, 0x15]),
        Raw(0x02D334, [0x8B, 0x35, 0x48, 0x4E, 0x88, 0x00], [0x8B, 0x35, 0x3C, 0x4E, 0x88, 0x00]),
        Raw(0x355D15,
        [
            0x83, 0xF8, 0x32, 0x7D, 0x03, 0x83, 0xC0, 0x01, 0x83, 0xF9, 0x32, 0x7D, 0x03, 0x83, 0xC1, 0x01,
            0xE9, 0xB8, 0x6F, 0xCD, 0xFF,
        ], [0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]),
        Raw(0x355DDC,
        [
            0x8D, 0x4D, 0xF0, 0x51, 0xFF, 0x35, 0x00, 0x4E, 0x88, 0x00, 0xFF, 0x15, 0x50, 0xF6, 0x7F, 0x00,
            0x8B, 0x45, 0xF0, 0x8B, 0x15, 0x44, 0x4E, 0x88, 0x00, 0xE9, 0x35, 0x75, 0xCD, 0xFF,
        ], [0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]),
    ];

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
            return
            [
                new(0x435D38, buffer, null, [[0x31, 0x32, 0x00, 0x00]]),
            ];
        },
        BuildQuestLogSteps = null
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
            BuildQuestLogSteps = null
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
                return
                [
                    new(offset, BitConverter.GetBytes((float)raw), null, acceptBeforeOptions, null)
                ];
            },
            BuildQuestLogSteps = null
        };

    private static PatchDefinition QuestTweak(string id, string name, string description, int offset, int length, int RVA, int imageSize, IReadOnlyList<QuestLogPatcher.QuestRangeStruct> questRanges, int min, int max, int def, bool isInteger = true)
        => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Category = PatchCategory.QuestLogTweak,
            Parameter = new PatchParameter { Min = min, Max = max, Default = def, IsInteger = isInteger },
            BuildSteps = null,
            BuildQuestLogSteps = v =>
            {
                int uiValue = v ?? def;
                return
                [
                    // create new QuestRangeStruct (binary section inside the EXE that needs to be patched) with the provided parameters and the UI value
                    new(offset, length, RVA, imageSize, questRanges)
                ];
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
                accept[i] = [acceptOriginals[i]];
            }
        }

        return new PatchStep(offset, [value], null, accept, acceptOriginals.Length > 0 ? [acceptOriginals[0]] : null);
    }

    /// <summary>
    /// Multi-byte fixed-offset write, verified against one pristine "before" sequence, which also
    /// becomes this step's <see cref="PatchStep.WriteOff"/> (see <see cref="Off"/>).
    /// </summary>
    private static PatchStep Raw(long offset, byte[] write, byte[]? acceptBefore = null)
        => new(offset, write, null, acceptBefore is null ? null : [acceptBefore], acceptBefore);
}
