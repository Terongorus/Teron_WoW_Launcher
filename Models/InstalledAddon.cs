using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TeronWoWLauncher.Models;

public enum AddonSourceKind
{
    GitHub,
    Archive,

    /// <summary>Adopted from a folder that was already present in AddOns (installed outside the launcher).</summary>
    Manual,

    // Appended, not inserted before Manual - SourceKind serializes as a raw int (no string enum
    // converter), so any existing addons.json on disk already has real 0/1/2 values baked in;
    // inserting a new value earlier in this list would silently reinterpret every already-installed
    // addon's stored SourceKind as the wrong kind.
    GitLab,
}

/// <summary>A tracked addon installed into Interface\AddOns, persisted in addons.json.</summary>
public sealed class InstalledAddon
{
    /// <summary>Raw display name, possibly containing WoW color codes (from the .toc's "## Title:", or the folder name).</summary>
    public required string Name { get; set; }

    public AddonSourceKind SourceKind { get; set; }

    /// <summary>Canonical source reference (repo URL, or the original archive path/URL). Null for Manual.</summary>
    public string? SourceRef { get; set; }

    /// <summary>The addon's own version, from its .toc "## Version:" line. Null if the .toc has none - VersionDisplay shows "v?" in that case rather than hiding the field entirely.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Opaque signature of the remote source at the time this was last installed/updated (a GitHub
    /// release tag/commit sha, or a direct URL's size+ETag). Used only to detect updates — never displayed.
    /// </summary>
    public string? RemoteVersionSignature { get; set; }

    /// <summary>The AddOns subfolder names this addon installed (an archive may contain several).</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>
    /// User-chosen on-disk folder names, keyed by the repo/.toc-derived canonical name they replace.
    /// Applied on every future install/update (see <c>AddonInstaller.InstallFromDirectory</c>) so a
    /// rename made via the launcher's UI survives updates instead of being overwritten by the
    /// canonical name on the next re-install — <see cref="SourceRef"/> is untouched by a rename, so
    /// update-checking keeps comparing against the same remote regardless.
    /// </summary>
    public Dictionary<string, string> FolderRenames { get; set; } = new();

    public DateTime InstalledUtc { get; set; }

    /// <summary>User opt-out, set from the Details dialog: never flag this addon as updatable, even if a newer remote version is found.</summary>
    public bool IgnoreUpdates { get; set; }

    /// <summary>Set by <c>AddonLibrary.CheckForUpdatesAsync</c>; never persisted or true for Manual addons.</summary>
    [JsonIgnore]
    public bool HasUpdateAvailable { get; set; }

    /// <summary>Prefixes a single "v" - some addons' own "## Version:" line already starts with one
    /// (e.g. "v1.0.0"), which used to produce a doubled "vv1.0.0"; that leading v/V is stripped
    /// first so exactly one is ever shown regardless of what the .toc itself contains.</summary>
    [JsonIgnore]
    public string VersionDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(Version))
            {
                return "v?";
            }

            string trimmed = Version.StartsWith('v') || Version.StartsWith('V') ? Version[1..] : Version;
            return $"v{trimmed}";
        }
    }

    /// <summary>
    /// Human-facing source name shown in the addon list. GitHub/GitLab/Manual map straight from
    /// SourceKind, but Archive alone doesn't say enough - LegacyWowAddonSource and WarperiaAddonSource
    /// both resolve down to a plain archive download and report Kind=Archive too (see their own doc
    /// comments), so the actual site has to be read back out of SourceRef's host. A generic direct
    /// archive URL or local file path (no recognized host) is "Local", same as a Manual adoption -
    /// both mean "no site the launcher can re-check for updates through its own source resolvers."
    /// </summary>
    [JsonIgnore]
    public string SourceLabel => SourceKind switch
    {
        AddonSourceKind.GitHub => "GitHub",
        AddonSourceKind.GitLab => "GitLab",
        AddonSourceKind.Archive when SourceRef?.Contains("warperia.com", StringComparison.OrdinalIgnoreCase) == true => "Warperia",
        AddonSourceKind.Archive when SourceRef?.Contains("legacy-wow.com", StringComparison.OrdinalIgnoreCase) == true => "LegacyWoW",
        _ => "Local",
    };
}
