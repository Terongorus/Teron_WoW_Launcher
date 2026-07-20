using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Patching.MpqReader;

namespace TeronWoWLauncher.Services.Patching;

/// <summary>
/// Extraction result. Title is only ever set from an explicit "## Title:" field in a bundled
/// Patch.toc (see TocMetadataReader for the identical convention used by addon .toc files) - never
/// guessed from free-text readme content, which stays too unreliable a heuristic to trust for a
/// display title. Author/Description can come from either Patch.toc or a plain readme-style file.
/// Version/Website only ever come from Patch.toc, and are kept separate from Description
/// specifically so they land in MpqPatchMetadata's own extraction-only fields rather than inside the
/// user-editable Description text - see MpqPatchMetadata's doc comment for why that separation
/// matters (a manual Description edit must never be able to silently corrupt/lose them).
/// </summary>
public readonly record struct MpqExtractedInfo(string? Title, string? Author, string? Description, string? Version, string? Website);

/// <summary>
/// Best-effort peek inside a custom MPQ patch for known metadata sources, using the vendored
/// MpqReader (Services/Patching/MpqReader/ - adapted from the MIT-licensed War3Net.IO.Mpq, not a
/// NuGet dependency, since that package transitively pulls in DotNetZip - see MpqReader's own doc
/// comments for why a minimal in-repo reader was preferred over taking the package, and what it
/// deliberately does and doesn't support). Most small hand-made vanilla-WoW patches carry no
/// embedded "(listfile)" (that's an optional convenience some archive tools add — the game itself
/// addresses files by known hardcoded names, not by enumerating), so this checks hardcoded candidate
/// names via direct hash-based lookup rather than enumerating the archive.
///
/// Checks a "Patch.toc" first (confirmed 2026-07-19 against a real content patch, "Project Reforged
/// - Darker Nights" - larger, asset-replacement-style patches built with modern tooling apparently
/// follow the exact same "## Title:"/"## Version:"/"## Website:" convention as addon .toc files,
/// which is far more reliable than scraping free text). Falls back to a handful of common
/// readme/info-style files only when no Patch.toc (or an empty one) is present.
/// </summary>
public static class MpqPatchMetadataExtractor
{
    private const string PatchTocFileName = "Patch.toc";

    private static readonly string[] ReadmeCandidateFileNames =
    {
        "readme.txt", "readme.md", "info.txt", "description.txt",
        "changelog.txt", "credits.txt", "notes.txt",
    };

    // First match wins on a line like "Author: Foo" / "By: Foo" / "Created by: Foo" — deliberately
    // simple; a false negative (Author stays null) is a fully acceptable degrade for best-effort.
    private static readonly Regex AuthorLineRegex = new(
        @"^\s*(?:-\s*)?(?:author|by|created\s*by|made\s*by)\s*[:\-]\s*(?<name>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private const int DescriptionMaxLength = 500;

    /// <summary>Never throws — a corrupt/unreadable/empty archive is a fully expected outcome
    /// against arbitrary user-supplied files, not an error worth surfacing to the user. Returns null
    /// whenever nothing usable was found, letting the caller fall back to filename-only display.</summary>
    public static MpqExtractedInfo? TryExtract(string mpqPath)
    {
        try
        {
            using MpqArchiveReader archive = MpqArchiveReader.Open(mpqPath);

            MpqExtractedInfo? fromToc = TryExtractPatchToc(archive);
            if (fromToc is not null)
            {
                return fromToc;
            }

            return TryExtractReadmeStyle(archive);
        }
        catch (MpqParserException ex)
        {
            Logger.Instance.Warn($"MPQ patch '{Path.GetFileName(mpqPath)}' could not be parsed for metadata (corrupt/unsupported header): {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            // Any other unexpected failure (I/O, an edge case in the library, an unreadable codepage,
            // etc.) — soft-log only, never a user-facing toast: this runs for every patch on every
            // add/retroactive pass, and "nothing found" is the overwhelmingly common expected outcome.
            Logger.Instance.Warn($"Unexpected error extracting metadata from '{Path.GetFileName(mpqPath)}': {ex.Message}");
            return null;
        }
    }

    private static MpqExtractedInfo? TryExtractPatchToc(MpqArchiveReader archive)
    {
        using Stream? stream = archive.OpenFile(PatchTocFileName);
        if (stream is null)
        {
            return null;
        }

        string? title = null;
        string? author = null;
        string? description = null;
        string? version = null;
        string? website = null;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (TryReadTocField(trimmed, "## Title:", out string? t))
            {
                title = t;
            }
            else if (TryReadTocField(trimmed, "## Author:", out string? a))
            {
                author = a;
            }
            // .toc files (both addon .toc and this same Patch.toc convention) use "## Notes:" for
            // the description-equivalent field, not "## Description:" - mapped straight into the
            // Description property/UI label, which keeps its existing name regardless.
            else if (TryReadTocField(trimmed, "## Notes:", out string? n))
            {
                description = n;
            }
            else if (TryReadTocField(trimmed, "## Version:", out string? v))
            {
                version = v;
            }
            else if (TryReadTocField(trimmed, "## Website:", out string? w))
            {
                website = w;
            }
        }

        if (title is null && author is null && description is null && version is null && website is null)
        {
            return null;
        }

        return new MpqExtractedInfo(title, author, description, version, website);
    }

    private static bool TryReadTocField(string line, string prefix, out string? value)
    {
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string v = line[prefix.Length..].Trim();
            value = string.IsNullOrWhiteSpace(v) ? null : v;
            return true;
        }

        value = null;
        return false;
    }

    private static MpqExtractedInfo? TryExtractReadmeStyle(MpqArchiveReader archive)
    {
        foreach (string candidate in ReadmeCandidateFileNames)
        {
            using Stream? stream = archive.OpenFile(candidate);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            string? author = null;
            Match m = AuthorLineRegex.Match(text);
            if (m.Success)
            {
                author = m.Groups["name"].Value.Trim();
            }

            string description = Regex.Replace(text.Trim(), @"\s+", " ");
            if (description.Length > DescriptionMaxLength)
            {
                description = description[..DescriptionMaxLength].TrimEnd() + "…";
            }

            return new MpqExtractedInfo(null, author, string.IsNullOrWhiteSpace(description) ? null : description, null, null);
        }

        return null; // opened fine, nothing recognizable inside — genuinely empty, not an error
    }
}
