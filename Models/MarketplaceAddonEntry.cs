using System;

namespace TeronWoWLauncher.Models;

/// <summary>
/// One addon listing scraped from a marketplace provider's catalog (Legacy-WoW or Warperia) — not
/// yet installed, just discoverable. <see cref="DetailUrl"/> is the same detail-page URL format
/// <c>LegacyWowAddonSource</c>/<c>WarperiaAddonSource</c> already resolve for installing, so
/// browsing and installing share the exact same downstream path.
/// </summary>
/// <param name="Category">Null for providers/entries where a category isn't available (see
/// MarketplaceService's per-provider notes) — sort/filter logic must treat this as "unknown",
/// never fabricate one.</param>
/// <param name="Downloads">Null where the provider doesn't expose a download count.</param>
/// <param name="LastUpdated">Null where the provider doesn't expose a last-updated date.</param>
/// <param name="ThumbnailUrl">Null where the entry has no thumbnail, or the provider's thumbnail
/// couldn't be resolved.</param>
public sealed record MarketplaceAddonEntry(
    string Name,
    string? Author,
    string? Description,
    string DetailUrl,
    string Source,
    string? Category,
    int? Downloads,
    DateTimeOffset? LastUpdated,
    string? ThumbnailUrl);
