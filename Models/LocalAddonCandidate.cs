using TeronWoWLauncher.Services;

namespace TeronWoWLauncher.Models;

/// <summary>
/// An AddOns folder found on disk that isn't tracked by the launcher yet — either installed by hand,
/// or left over from before the addon manager existed. Offered to the user for adoption so it shows
/// up in the tracked list without being reinstalled.
/// </summary>
public sealed class LocalAddonCandidate
{
    public required string FolderName { get; init; }

    /// <summary>From the .toc's "## Title:" line, if present.</summary>
    public string? Title { get; init; }

    /// <summary>From the .toc's "## Version:" line, if present.</summary>
    public string? Version { get; init; }

    public string Display => string.IsNullOrEmpty(Title)
        ? FolderName
        : $"{WowColorTextParser.StripCodes(Title)}  ({FolderName})";
}
