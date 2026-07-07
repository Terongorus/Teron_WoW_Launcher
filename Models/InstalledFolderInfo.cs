namespace TeronWoWLauncher.Models;

/// <summary>An AddOns folder just installed from an archive, with whatever its own .toc reports.</summary>
public sealed record InstalledFolderInfo(string Folder, string? Title, string? Version);
