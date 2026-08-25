namespace TeronWoWLauncher.Models;

/// <summary>
/// One entry in the global, shared table of known WoW servers/clients (<see cref="LauncherSettings.ClientProfiles"/>).
/// A WoW directory doesn't store its client type/URL directly anymore - it just references a profile
/// by <see cref="Id"/> (<see cref="DirectorySettings.ClientProfileId"/>), the same way several
/// directories can point at the same server without re-entering its download URL each time.
/// </summary>
public sealed class ClientProfile
{
    /// <summary>Stable, internal, never shown in the UI. Generated once when the profile is created
    /// and never changed - this is what <see cref="DirectorySettings.ClientProfileId"/> actually
    /// stores, so renaming a profile's <see cref="Name"/> never orphans a directory pointing at it.</summary>
    public required string Id { get; set; }

    /// <summary>User-facing name shown in every dropdown/table - freely editable.</summary>
    public required string Name { get; set; }

    /// <summary>Where to download this client's archive from. Null when no fresh download exists
    /// (e.g. the built-in TurtleWoW seed - its server has shut down, so this only supports an
    /// already-installed copy, not a new install).</summary>
    public string? DownloadUrl { get; set; }

    public ClientCategory Category { get; set; }

    // --- Fixed ids for the launcher's own built-in seed profiles (created once, on first run, if the
    // table is empty - see MainWindow.SeedDefaultClientProfilesIfNeeded). Kept as constants so
    // PatchCatalog can layer each seed's own byte-verified starting values (e.g. OctoWoW's 3000-yard
    // farclip) on top of the generic Vanilla+ baseline without exposing that lookup in the UI - a
    // user-created custom profile simply won't match any of these and falls back to the generic
    // baseline instead. ---
    public const string KronosSeedId = "seed-kronos";
    public const string OctoWowSeedId = "seed-octowow";
    public const string TurtleWowSeedId = "seed-turtle-wow";
}
