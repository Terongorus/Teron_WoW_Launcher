namespace TeronWoWLauncher.Models;

/// <summary>
/// An AddOns folder just installed from an archive, with whatever its own .toc reports.
/// </summary>
/// <param name="Folder">The actual on-disk folder name — the repo/.toc-derived name, unless the
/// addon has a user rename recorded in <see cref="InstalledAddon.FolderRenames"/>, in which case
/// this is the renamed name.</param>
/// <param name="CanonicalFolder">The repo/.toc-derived name regardless of any rename — stable
/// across installs, used to match this folder back to the addon's own <see cref="InstalledAddon
/// .FolderRenames"/> entry and to <c>AddonDownload.SuggestedName</c>.</param>
public sealed record InstalledFolderInfo(string Folder, string? Title, string? Version, string CanonicalFolder);
