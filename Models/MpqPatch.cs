namespace TeronWoWLauncher.Models;

/// <summary>
/// A custom MPQ patch in the game's Data folder, identified by its slot letter (A–Z). Base Blizzard
/// patches (patch.mpq, patch-2.mpq, locale patches) are never represented here — only custom ones.
/// Enabled on disk as <c>patch-&lt;letter&gt;.mpq</c>; disabled by prefixing an underscore
/// (<c>_patch-&lt;letter&gt;.mpq</c>) so the client skips loading it.
/// </summary>
public sealed class MpqPatch
{
    /// <summary>Slot letter A–Z (upper-cased).</summary>
    public required string Letter { get; init; }

    /// <summary>The file's current on-disk name.</summary>
    public required string FileName { get; init; }

    /// <summary>True when loaded by the client (no leading underscore).</summary>
    public required bool Enabled { get; init; }
}
