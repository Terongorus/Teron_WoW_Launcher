namespace TeronWoWLauncher.Models;

/// <summary>
/// Best-effort metadata for a custom MPQ patch, keyed by its slot Letter (see MpqPatch) — the only
/// stable identity a patch has. Title/Author/Description/Version each follow the same rule: when a
/// value came from the patch's own bundled Patch.toc (or a readme-style file, for
/// Author/Description), the dialog shows it read-only, since that's authoritative data baked into
/// the patch, not something a local edit should diverge from. A field only becomes user-editable
/// once nothing was extracted for it (see MpqPatchMetadataDialog + each field's own *IsExtracted
/// flag). Website is the one exception - extraction-only, never exposed as an editable field at all,
/// since there's no meaningful "add your own website" case the way there's a "add your own
/// title/version" one. Persisted to .teronwow-mpq-metadata.json inside the WoW directory, mirroring
/// DirectorySettings' per-directory sidecar pattern.
/// </summary>
public sealed class MpqPatchMetadata
{
    public required string Letter { get; init; }

    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }

    /// <summary>True when Title's current value came from the patch's own Patch.toc rather than a
    /// manual edit - MpqPatchMetadataDialog makes the Title field read-only whenever this is true,
    /// and MpqPatchMetadataService.ApplyExtracted only ever refreshes Title while this stays true
    /// (i.e. never after the user has set their own), so a manual title is never silently
    /// overwritten by a later extraction pass.</summary>
    public bool TitleIsExtracted { get; set; }

    /// <summary>Same rule as TitleIsExtracted, for Author.</summary>
    public bool AuthorIsExtracted { get; set; }

    /// <summary>Same rule as TitleIsExtracted, for Description.</summary>
    public bool DescriptionIsExtracted { get; set; }

    /// <summary>Same rule as TitleIsExtracted, for Version.</summary>
    public bool VersionIsExtracted { get; set; }

    /// <summary>Extraction-only - never settable through MpqPatchMetadataDialog.</summary>
    public string? Website { get; set; }

    /// <summary>
    /// True once auto-extraction has been attempted for this slot (successful, found nothing, or
    /// the archive couldn't even be opened) — so a patch with genuinely nothing inside isn't
    /// re-opened and re-scanned on every refresh/restart/directory-switch. A manual edit also sets
    /// this, so extraction can never later overwrite what the user typed.
    /// </summary>
    public bool AutoExtractAttempted { get; set; }

    /// <summary>
    /// The .mpq file's size and last-write-time (UTC ticks) at the moment extraction last ran -
    /// lets MpqPatchMetadataService.NeedsExtraction detect that the user replaced this slot's file
    /// with a different/updated build (e.g. a newer version of the same content patch) even though
    /// AutoExtractAttempted is already true, so Version/Website don't go stale forever. Bookkeeping
    /// only, never shown in the UI.
    /// </summary>
    public long? ExtractedFileSize { get; set; }

    public long? ExtractedFileWriteTimeUtcTicks { get; set; }
}
