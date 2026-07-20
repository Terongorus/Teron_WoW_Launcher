namespace TeronWoWLauncher.Models;

/// <summary>
/// Manual-edit fallback for a DLL's Version/Author/Description, keyed by filename (the only stable
/// identity a DLL entry has — same reasoning as MpqPatchMetadata being keyed by slot Letter).
///
/// Unlike MPQ patch metadata, this never caches "was this extracted" bookkeeping: reading a DLL's own
/// Win32 version resource (DllMetadataReader) is cheap enough to just redo on every refresh, so
/// whether a field is "locked" (the DLL defines its own) is always decided live by checking that
/// live read, never persisted here. This sidecar only ever holds what the user actually typed for
/// whichever fields their DLL didn't already supply — see DllMetadataService.Merge. Many small
/// hand-built mod DLLs (exactly the kind this launcher injects) carry no version resource at all, so
/// every field here is commonly populated; DLLs that do carry their own metadata (e.g. DXVK's
/// d3d9.dll) simply never need an entry.
///
/// Persisted to .teronwow-dll-metadata.json inside the WoW directory, mirroring
/// DirectorySettings/MpqPatchMetadata's own per-directory sidecar pattern.
/// </summary>
public sealed class DllMetadata
{
    public required string Name { get; init; }

    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
}
