using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TeronWoWLauncher.Models;

public enum AddonSourceKind
{
    GitHub,
    Archive,
}

/// <summary>A tracked addon installed into Interface\AddOns, persisted in addons.json.</summary>
public sealed class InstalledAddon
{
    public required string Name { get; set; }

    public AddonSourceKind SourceKind { get; set; }

    /// <summary>Canonical source reference (repo URL, or the original archive path/URL).</summary>
    public string? SourceRef { get; set; }

    /// <summary>Release tag, commit, or date used to detect updates.</summary>
    public string? Version { get; set; }

    /// <summary>The AddOns subfolder names this addon installed (an archive may contain several).</summary>
    public List<string> Folders { get; set; } = new();

    public DateTime InstalledUtc { get; set; }

    [JsonIgnore]
    public string Display => $"{Name}   ({(string.IsNullOrEmpty(Version) ? "?" : Version)})   —   {SourceKind}";
}
