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
}

/// <summary>A tracked addon installed into Interface\AddOns, persisted in addons.json.</summary>
public sealed class InstalledAddon
{
    /// <summary>Raw display name, possibly containing WoW color codes (from the .toc's "## Title:", or the folder name).</summary>
    public required string Name { get; set; }

    public AddonSourceKind SourceKind { get; set; }

    /// <summary>Canonical source reference (repo URL, or the original archive path/URL). Null for Manual.</summary>
    public string? SourceRef { get; set; }

    /// <summary>The addon's own version, from its .toc "## Version:" line. Null if the .toc has none — never shown.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Opaque signature of the remote source at the time this was last installed/updated (a GitHub
    /// release tag/commit sha, or a direct URL's size+ETag). Used only to detect updates — never displayed.
    /// </summary>
    public string? RemoteVersionSignature { get; set; }

    /// <summary>The AddOns subfolder names this addon installed (an archive may contain several).</summary>
    public List<string> Folders { get; set; } = new();

    public DateTime InstalledUtc { get; set; }

    /// <summary>User opt-out, set from the Details dialog: never flag this addon as updatable, even if a newer remote version is found.</summary>
    public bool IgnoreUpdates { get; set; }

    /// <summary>Set by <c>AddonLibrary.CheckForUpdatesAsync</c>; never persisted or true for Manual addons.</summary>
    [JsonIgnore]
    public bool HasUpdateAvailable { get; set; }

    [JsonIgnore]
    public string VersionDisplay => string.IsNullOrEmpty(Version) ? string.Empty : $"v{Version}";
}
