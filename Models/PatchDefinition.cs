using System;
using System.Collections.Generic;

namespace TeronWoWLauncher.Models;

/// <summary>Which stage of the fixed patch order a patch belongs to. Lower value = applied first.</summary>
public enum PatchCategory
{
    /// <summary>Applied first: disables the client's file-integrity/signature checks.</summary>
    SignatureRemoval = 0,

    /// <summary>Applied second: the quality-of-life executable tweaks.</summary>
    VanillaTweak = 1,
}

/// <summary>
/// A single byte-level edit. Two shapes are supported:
///
///   * <b>Offset edit</b> (<see cref="Offset"/> &gt;= 0): write <see cref="Write"/> at that fixed
///     file offset. Reliable because we always patch the known pristine client. When
///     <see cref="AcceptBefore"/> is set, the current bytes must match one of those sequences (a
///     build-safety fingerprint) unless they already equal <see cref="Write"/>.
///
///   * <b>Pattern edit</b> (<see cref="Offset"/> &lt; 0): locate <see cref="Find"/> in the image and
///     overwrite it with <see cref="Write"/> (same length). <see cref="Find"/> also acts as the
///     pristine "before" state for detection.
/// </summary>
public sealed class PatchStep
{
    public long Offset { get; init; } = -1;

    public byte[]? Find { get; init; }

    public required byte[] Write { get; init; }

    /// <summary>Offset mode only: acceptable pre-patch byte sequences at the offset. Null = no check.</summary>
    public byte[][]? AcceptBefore { get; init; }

    /// <summary>
    /// Offset mode only: the bytes to write back when the owning patch is disabled during a rebuild.
    /// Null means "leave whatever the pristine backup already has" - correct for a genuinely pristine
    /// backup, where disabled and the backup's own value already coincide. Non-null is for patches
    /// whose backup can legitimately already contain the "on" bytes (e.g. a community client that
    /// ships a tweak baked in) - disabling then means actively reverting to this value rather than
    /// merely skipping, since skipping would silently leave the baked-in value in place.
    /// </summary>
    public byte[]? WriteOff { get; init; }
}

/// <summary>Optional numeric knob for a patch (e.g. render distance, nameplate range).</summary>
public sealed class PatchParameter
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Default { get; init; }

    /// <summary>True when the value is shown/edited as a whole number (every current tweak).</summary>
    public bool IsInteger { get; init; }

    /// <summary>Optional unit shown in the UI, e.g. "yds" or "rad".</summary>
    public string? Unit { get; init; }
}

/// <summary>
/// One user-selectable patch. Its concrete byte edits are produced by <see cref="BuildSteps"/>,
/// which receives the chosen parameter value (null for simple on/off patches). Keeping the edits
/// behind a factory lets a parameterized patch encode the user's value into the replacement bytes.
/// </summary>
public sealed class PatchDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required PatchCategory Category { get; init; }

    /// <summary>
    /// True for the small set of tweaks that should be grouped and labeled as required reading before
    /// using custom MPQ patches (Signature Removal, Large Address Aware) - orthogonal to
    /// <see cref="Category"/>, which only controls WoW.exe rebuild order. Purely a UI grouping/labeling
    /// concern; it has no effect on patching behavior itself.
    /// </summary>
    public bool MandatoryForMpq { get; init; }

    /// <summary>
    /// True for a patch whose pristine backup can legitimately already contain its "on" bytes (a
    /// community client tweak baked in at the factory) - a fresh directory pre-checks this patch's
    /// checkbox instead of leaving it unchecked like everything else, so the first sync is a no-op
    /// against what the client already ships rather than an unrequested revert. See
    /// <see cref="DirectorySettings.PatchDefaultsSeeded"/> for how this is applied only once.
    /// </summary>
    public bool DefaultEnabled { get; init; }

    /// <summary>Non-null when the patch exposes a numeric value to the user.</summary>
    public PatchParameter? Parameter { get; init; }

    /// <summary>Build the concrete edits for this patch given the chosen parameter value (if any).</summary>
    public required Func<double?, IReadOnlyList<PatchStep>> BuildSteps { get; init; }
}
