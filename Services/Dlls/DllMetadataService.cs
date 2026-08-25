using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services.Core;

namespace TeronWoWLauncher.Services.Dlls;

/// <summary>
/// Loads and saves a Name-keyed dictionary of <see cref="DllMetadata"/> as JSON inside a specific WoW
/// directory (&lt;wowDir&gt;\.teronwow-dll-metadata.json) — mirrors
/// <see cref="Patching.MpqPatchMetadataService"/>'s own Current/Load/Save shape, scoped per
/// installation the same way. Name (the DLL's filename/dlls.txt entry) is the only stable identity a
/// DLL has, same reasoning as MPQ patches being keyed by slot Letter.
/// </summary>
public sealed class DllMetadataService
{
    private const string FileName = ".teronwow-dll-metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _log = Logger.Instance;

    public Dictionary<string, DllMetadata> Current { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath(string wowDir) => PerDirectoryDataFolder.ResolvePath(wowDir, FileName);

    public Dictionary<string, DllMetadata> Load(string wowDir)
    {
        string path = FilePath(wowDir);
        try
        {
            if (File.Exists(path))
            {
                Dictionary<string, DllMetadata>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, DllMetadata>>(File.ReadAllText(path));
                Current = loaded ?? new Dictionary<string, DllMetadata>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                Current = new Dictionary<string, DllMetadata>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load DLL metadata; using defaults. {ex.Message}");
            Current = new Dictionary<string, DllMetadata>(StringComparer.OrdinalIgnoreCase);
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
            _log.Error($"Failed to save DLL metadata: {ex.Message}");
        }
    }

    public DllMetadata? Get(string name) => Current.TryGetValue(name, out DllMetadata? m) ? m : null;

    /// <summary>Combines a DLL's own live-read version-resource info (<paramref name="extracted"/>,
    /// from DllMetadataReader.Read) with whatever manual fallback is on file for whichever fields it
    /// left blank — the live read always wins when present, exactly like MpqPatchMetadata's
    /// extraction-lock rule, just decided fresh every call instead of persisted.</summary>
    public DllInfo Merge(DllInfo extracted, bool isTracked)
    {
        DllMetadata? manual = Get(extracted.Name);
        return new DllInfo
        {
            Name = extracted.Name,
            Version = extracted.Version ?? manual?.Version,
            Author = extracted.Author ?? manual?.Author,
            Description = extracted.Description ?? manual?.Description,
            IsTracked = isTracked,
        };
    }

    /// <summary>Version/Author/Description each take the submitted value only when their
    /// corresponding *IsUserEditable flag is true (the dialog only allows editing a field the live
    /// version-resource read left blank) — when false, that field's existing stored value (if any) is
    /// preserved untouched rather than overwritten by whatever the disabled box displayed, matching
    /// MpqPatchMetadataService.SaveManualEdit's same rule. A record that ends up with nothing set at
    /// all is removed outright rather than persisted as an empty stub, keeping the sidecar limited to
    /// DLLs that actually needed a manual entry.</summary>
    public void SaveManualEdit(
        string wowDir, string name,
        string? version, bool versionIsUserEditable,
        string? author, bool authorIsUserEditable,
        string? description, bool descriptionIsUserEditable)
    {
        Current.TryGetValue(name, out DllMetadata? existing);
        var updated = new DllMetadata
        {
            Name = name,
            Version = versionIsUserEditable ? NullIfBlank(version) : existing?.Version,
            Author = authorIsUserEditable ? NullIfBlank(author) : existing?.Author,
            Description = descriptionIsUserEditable ? NullIfBlank(description) : existing?.Description,
        };

        if (updated.Version is null && updated.Author is null && updated.Description is null)
        {
            if (Current.Remove(name))
            {
                Save(wowDir);
            }

            return;
        }

        Current[name] = updated;
        Save(wowDir);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
