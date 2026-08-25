using System.Collections.Generic;

namespace TeronWoWLauncher.Models;

/// <summary>A DLL list entry (tracked or detected) plus whatever version-resource metadata it carries.</summary>
public sealed class DllInfo
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }

    /// <summary>True when this entry is currently in dlls.txt (injected on launch); false when it was
    /// only found sitting in the game folder. Drives the merged DLLs-tab list's grouping and each
    /// row's track/untrack and ignore buttons - purely a UI-facing flag, not persisted anywhere
    /// itself (dlls.txt's own contents are what's authoritative).</summary>
    public bool IsTracked { get; init; }

    /// <summary>
    /// Version/author/description joined for the list row, omitting whatever wasn't found — many
    /// small hand-built DLLs (exactly the kind this launcher injects) don't carry a full version
    /// resource, so this degrades gracefully down to an empty string when none of it is present.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Version))
            {
                parts.Add($"v{Version}");
            }

            if (!string.IsNullOrWhiteSpace(Author))
            {
                parts.Add(Author);
            }

            if (!string.IsNullOrWhiteSpace(Description))
            {
                parts.Add(Description);
            }

            return string.Join("  •  ", parts);
        }
    }
}
