using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services.Core;

namespace TeronWoWLauncher.Services.Patching;

/// <summary>
/// Loads and saves a Letter-keyed dictionary of <see cref="MpqPatchMetadata"/> as JSON inside a
/// specific WoW directory (&lt;wowDir&gt;\.teronwow-mpq-metadata.json) — mirrors
/// <see cref="Core.DirectorySettingsService"/>'s own Current/Load/Save shape, scoped per
/// installation the same way. Letter is the only stable identity an <see cref="MpqPatch"/> has (see
/// MpqPatchService), so it's the natural key here too.
/// </summary>
public sealed class MpqPatchMetadataService
{
    private const string FileName = ".teronwow-mpq-metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _log = Logger.Instance;

    public Dictionary<string, MpqPatchMetadata> Current { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath(string wowDir) => PerDirectoryDataFolder.ResolvePath(wowDir, FileName);

    public Dictionary<string, MpqPatchMetadata> Load(string wowDir)
    {
        string path = FilePath(wowDir);
        try
        {
            if (File.Exists(path))
            {
                Dictionary<string, MpqPatchMetadata>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, MpqPatchMetadata>>(File.ReadAllText(path));
                Current = loaded ?? new Dictionary<string, MpqPatchMetadata>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                Current = new Dictionary<string, MpqPatchMetadata>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load MPQ patch metadata; using defaults. {ex.Message}");
            Current = new Dictionary<string, MpqPatchMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        return Current;
    }

    public void Save(string wowDir)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath(wowDir), JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save MPQ patch metadata: {ex.Message}");
        }
    }

    public MpqPatchMetadata? Get(string letter) => Current.TryGetValue(letter, out MpqPatchMetadata? m) ? m : null;

    /// <summary>True when this slot has never had an extraction attempt recorded, OR the file at
    /// <paramref name="filePath"/> has a different size/last-write-time than it did when extraction
    /// last ran - the user replaced this slot with a different/updated build (e.g. a newer version
    /// of the same content patch), so Version/Website shouldn't be left stuck on stale data forever
    /// just because AutoExtractAttempted is already true.</summary>
    public bool NeedsExtraction(string letter, string filePath)
    {
        if (!Current.TryGetValue(letter, out MpqPatchMetadata? m) || !m.AutoExtractAttempted)
        {
            return true;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return false; // nothing to (re-)scan
            }

            return m.ExtractedFileSize != info.Length || m.ExtractedFileWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return false; // can't stat the file right now - don't force a re-scan over a transient error
        }
    }

    /// <summary>Title/Author/Description/Version each take the submitted value only when their
    /// corresponding *IsUserEditable flag is true (the dialog only allows editing a field when
    /// nothing was extracted for it) - when false, that field and its provenance flag are preserved
    /// untouched regardless of whatever the (read-only) box displayed, so a locked, patch-defined
    /// value can never be accidentally reclassified as manual just by opening and saving the dialog.
    /// Website/the extraction fingerprint are always preserved here too - this dialog has no field
    /// for Website at all, so a full-object replacement would otherwise silently wipe out whatever
    /// was already extracted.</summary>
    public void SaveManualEdit(
        string wowDir, string letter,
        string? title, bool titleIsUserEditable,
        string? author, bool authorIsUserEditable,
        string? description, bool descriptionIsUserEditable,
        string? version, bool versionIsUserEditable)
    {
        Current.TryGetValue(letter, out MpqPatchMetadata? existing);
        var updated = new MpqPatchMetadata
        {
            Letter = letter,
            Website = existing?.Website,
            ExtractedFileSize = existing?.ExtractedFileSize,
            ExtractedFileWriteTimeUtcTicks = existing?.ExtractedFileWriteTimeUtcTicks,
            AutoExtractAttempted = true,
        };

        ApplyEditableField(titleIsUserEditable, title, existing?.Title, existing?.TitleIsExtracted,
            out string? newTitle, out bool newTitleIsExtracted);
        updated.Title = newTitle;
        updated.TitleIsExtracted = newTitleIsExtracted;

        ApplyEditableField(authorIsUserEditable, author, existing?.Author, existing?.AuthorIsExtracted,
            out string? newAuthor, out bool newAuthorIsExtracted);
        updated.Author = newAuthor;
        updated.AuthorIsExtracted = newAuthorIsExtracted;

        ApplyEditableField(descriptionIsUserEditable, description, existing?.Description, existing?.DescriptionIsExtracted,
            out string? newDescription, out bool newDescriptionIsExtracted);
        updated.Description = newDescription;
        updated.DescriptionIsExtracted = newDescriptionIsExtracted;

        ApplyEditableField(versionIsUserEditable, version, existing?.Version, existing?.VersionIsExtracted,
            out string? newVersion, out bool newVersionIsExtracted);
        updated.Version = newVersion;
        updated.VersionIsExtracted = newVersionIsExtracted;

        Current[letter] = updated;
        Save(wowDir);
    }

    private void ApplyEditableField(bool isUserEditable, string? submittedValue, string? existingValue, bool? existingIsExtracted, out string? value, out bool isExtracted)
    {
        if (isUserEditable)
        {
            value = NullIfBlank(submittedValue);
            isExtracted = false;
        }
        else
        {
            value = existingValue;
            isExtracted = existingIsExtracted ?? false;
        }
    }

    /// <summary>Refreshes a field from extraction only when it's still unset or was itself
    /// extraction-derived (never after a manual edit has taken ownership of it).</summary>
    private static void ApplyExtractedField(string? extractedValue, string? existingValue, bool existingIsExtracted, out string? value, out bool isExtracted)
    {
        string? clean = NullIfBlank(extractedValue);
        if (clean is not null && (existingValue is null || existingIsExtracted))
        {
            value = clean;
            isExtracted = true;
        }
        else
        {
            value = existingValue;
            isExtracted = existingValue is not null && existingIsExtracted;
        }
    }

    /// <summary>Each of Title/Author/Description/Version only overwrites when it's still marked as
    /// extraction-derived (or unset) - so a version bump that changes the patch's own declared
    /// values is picked up, but a user's manual edit (that field's *IsExtracted false) never gets
    /// silently replaced by a later pass, including the case where NeedsExtraction returned true
    /// because the file changed rather than this being the first attempt. Website always overwrites
    /// unconditionally instead, since it has no manual-edit concept at all (see MpqPatchMetadata's
    /// doc comment) - it should always reflect whatever the most recent scan of the current file
    /// found.</summary>
    public void ApplyExtracted(string wowDir, string letter, string filePath, string? title, string? author, string? description, string? version, string? website)
    {
        if (!Current.TryGetValue(letter, out MpqPatchMetadata? existing))
        {
            existing = new MpqPatchMetadata { Letter = letter };
            Current[letter] = existing;
        }

        ApplyExtractedField(title, existing.Title, existing.TitleIsExtracted, out string? newTitle, out bool newTitleIsExtracted);
        existing.Title = newTitle;
        existing.TitleIsExtracted = newTitleIsExtracted;

        ApplyExtractedField(author, existing.Author, existing.AuthorIsExtracted, out string? newAuthor, out bool newAuthorIsExtracted);
        existing.Author = newAuthor;
        existing.AuthorIsExtracted = newAuthorIsExtracted;

        ApplyExtractedField(description, existing.Description, existing.DescriptionIsExtracted, out string? newDescription, out bool newDescriptionIsExtracted);
        existing.Description = newDescription;
        existing.DescriptionIsExtracted = newDescriptionIsExtracted;

        ApplyExtractedField(version, existing.Version, existing.VersionIsExtracted, out string? newVersion, out bool newVersionIsExtracted);
        existing.Version = newVersion;
        existing.VersionIsExtracted = newVersionIsExtracted;

        existing.Website = NullIfBlank(website);
        existing.AutoExtractAttempted = true;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
            {
                existing.ExtractedFileSize = info.Length;
                existing.ExtractedFileWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
            // Best-effort bookkeeping only - if this fails, NeedsExtraction just won't have a
            // fingerprint to compare against next time, which is equivalent to how a
            // never-before-attempted slot already behaves.
        }

        Save(wowDir);
    }

    /// <summary>Called alongside MpqPatchService.Remove so a freed letter reused by a later Add never
    /// inherits stale metadata from whatever used to occupy that slot.</summary>
    public void Remove(string wowDir, string letter)
    {
        if (Current.Remove(letter))
        {
            Save(wowDir);
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
