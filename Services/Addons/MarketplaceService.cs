using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services.Core;

namespace TeronWoWLauncher.Services.Addons;

/// <summary>One of Legacy-WoW's 26 addon categories (id + display name), for populating its Category dropdown.</summary>
public sealed record LegacyWowCategory(int Id, string Name);

/// <summary>
/// Fetches and caches the browsable addon catalogs for Legacy-WoW and Warperia (issue #8) — separate
/// from <c>Sources/LegacyWowAddonSource</c>/<c>WarperiaAddonSource</c>, which resolve one already-known
/// detail-page URL for installing; this resolves whole listing pages for browsing/searching.
///
/// Caching pattern mirrors Teron_Addon_Manager's EsoUiCatalogService: a small on-disk
/// { FetchedAt, Entries } JSON file per provider/category/page, with a lifetime and an explicit
/// forceRefresh escape hatch (wired to the Browse UI's Refresh button) that bypasses the cache read
/// entirely rather than just shortening it.
/// </summary>
public sealed class MarketplaceService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string CacheDirectory = Path.Combine(AppPaths.DataRoot, "MarketplaceCache");

    // Owned internally (not passed in) specifically so listing-page fetches and thumbnail-token
    // fetches always share the same cookie jar — Warperia's thumbnail tokens are gated the same
    // session-cookie-locked way its download tokens are (see #5), so the HttpClient that loaded the
    // listing page has to be the exact same one that later resolves each thumbnail token.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Logger _log = Logger.Instance;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeronWoWLauncher");
        return http;
    }

    /// <summary>Legacy-WoW's fixed 26 categories (id=137 is "All"), scraped once from its own filter dropdown — this list doesn't change without a site redesign, so it's hardcoded rather than re-scraped every time.</summary>
    public static readonly IReadOnlyList<LegacyWowCategory> LegacyWowCategories = new List<LegacyWowCategory>
    {
        new(137, "All"), new(138, "Accessories"), new(139, "Action Bars"), new(140, "Artwork"),
        new(141, "Auction & Economy"), new(142, "Audio & Video"), new(143, "Bags & Inventory"),
        new(144, "Boss Encounters"), new(145, "Buffs & Debuffs"), new(146, "Chat & Communications"),
        new(147, "Class"), new(148, "Combat"), new(149, "Companions"), new(150, "Data Export"),
        new(151, "Development Tools"), new(152, "Guild"), new(153, "Libraries"), new(154, "Mail"),
        new(155, "Map & Minimap"), new(156, "Miscellaneous"), new(157, "Professions"), new(158, "PvP"),
        new(159, "Quests & Leveling"), new(160, "Roleplay"), new(161, "Tooltip"), new(162, "Unit Frames"),
        new(163, "Addon Pack"),
    };

    /// <summary>Warperia's confirmed native sort values (its own "sort by" dropdown) — "az"/"za" have no server-side equivalent, handled client-side in the UI layer instead.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> WarperiaSortOptions = new List<(string, string)>
    {
        ("popularity", "Most Popular"),
        ("recent", "Recently Added"),
        ("updated", "Recently Updated"),
    };

    // Two-pass parse: grab each raw <tr>...</tr> block first, then extract fields from that
    // substring separately from the thumbnail. Doing it all in one regex with the thumbnail as an
    // optional inline group failed in practice — a lazy `.*?` engine prefers to skip an optional
    // group entirely rather than backtrack to satisfy it, so `data-original` silently never matched
    // even on rows that had it. Splitting the thumbnail into its own unconditional match on the
    // already-isolated row text sidesteps that entirely (verified against the real fetched page).
    private static readonly Regex LegacyWowRowRegex = new(@"<tr>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LegacyWowFieldsRegex = new(
        @"href=""(?<url>https://legacy-wow\.com/vanilla-addons/[^""]+/)"".*?<div class=""AddonTitleDesc"">\s*<a[^>]*>(?<name>[^<]+)</a>\s*</div>\s*<div>(?<desc>[^<]*)</div>.*?<center>(?<downloads>\d+)</center>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LegacyWowThumbRegex = new(@"data-original=""(?<thumb>[^""]+)""", RegexOptions.Compiled);

    private static readonly Regex WarperiaCardRegex = new(
        @"<a class=""card addon-card-wrapper[^""]*""\s*href=""(?<url>[^""]+)""\s*data-addon-id=""(?<id>\d+)"".*?data-src=""(?<thumb>[^""]+)"".*?addon-name-header[^>]*>\s*(?<name>[^<]+?)\s*(?:<span[^>]*>\s*by\s*(?<author>[^<]+)</span>)?\s*</div>.*?addon-description-text[^>]*>\s*(?<desc>[^<]*?)\s*</div>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Fetches Legacy-WoW's listing for one category (137 = All ≈ 700 addons, single page — no pagination to handle for any category, confirmed against real fetched pages).</summary>
    public async Task<IReadOnlyList<MarketplaceAddonEntry>> FetchLegacyWowCatalogAsync(
        int categoryId, CancellationToken ct, bool forceRefresh = false)
    {
        string cacheKey = $"legacywow_cat{categoryId}";
        if (!forceRefresh && TryLoadCache(cacheKey, out List<MarketplaceAddonEntry>? cached))
        {
            return cached!;
        }

        string url = $"https://legacy-wow.com/vanilla-addons/?category={categoryId}";
        string html = await Http.GetStringAsync(url, ct);

        // The regex parsing below (~700 rows for the "All" category) and the synchronous cache
        // write are genuine CPU/disk work, not I/O to await - left inline, they'd run on whatever
        // context resumed the GetStringAsync above (the UI thread, since this is always called from
        // a UI event handler), causing a visible stutter every time Browse loads. Task.Run moves
        // that work to the thread pool so the UI stays responsive.
        return await Task.Run(() =>
        {
            var entries = new List<MarketplaceAddonEntry>();
            foreach (Match rowMatch in LegacyWowRowRegex.Matches(html))
            {
                string row = rowMatch.Groups["row"].Value;
                Match m = LegacyWowFieldsRegex.Match(row);
                if (!m.Success)
                {
                    continue;
                }

                string name = WebUtility.HtmlDecode(m.Groups["name"].Value).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                Match thumbMatch = LegacyWowThumbRegex.Match(row);
                string? thumb = thumbMatch.Success ? WebUtility.HtmlDecode(thumbMatch.Groups["thumb"].Value) : null;
                string desc = WebUtility.HtmlDecode(m.Groups["desc"].Value).Trim();
                int? downloads = int.TryParse(m.Groups["downloads"].Value, out int d) ? d : null;

                entries.Add(new MarketplaceAddonEntry(
                    name, Author: null, Description: desc.Length == 0 ? null : desc,
                    DetailUrl: m.Groups["url"].Value, Source: "Legacy-WoW",
                    Category: categoryId == 137 ? null : LegacyWowCategories.FirstOrDefault(c => c.Id == categoryId)?.Name,
                    Downloads: downloads, LastUpdated: null, ThumbnailUrl: thumb));
            }

            SaveCache(cacheKey, entries);
            return (IReadOnlyList<MarketplaceAddonEntry>)entries;
        }, ct);
    }

    /// <summary>Fetches one page of Warperia's listing. Category filtering isn't supported for v1 — Warperia's own category system exists (checkbox-based, e.g. data-category="class") but its actual filtering mechanism isn't confirmed working (no per-entry category data on the cards, filter looked AJAX-driven rather than a simple URL param), so it's left out rather than guessed at.</summary>
    public async Task<IReadOnlyList<MarketplaceAddonEntry>> FetchWarperiaPageAsync(
        int page, string sort, CancellationToken ct, bool forceRefresh = false)
    {
        string cacheKey = $"warperia_p{page}_{sort}";
        if (!forceRefresh && TryLoadCache(cacheKey, out List<MarketplaceAddonEntry>? cached))
        {
            return cached!;
        }

        string url = $"https://warperia.com/vanilla-addons/?paged={page}&sort={sort}";
        string html = await Http.GetStringAsync(url, ct);

        // See the identical comment in FetchLegacyWowCatalogAsync: this offloads the regex parsing
        // and synchronous cache write to the thread pool so it can't stutter the UI thread.
        return await Task.Run(() =>
        {
            var entries = new List<MarketplaceAddonEntry>();
            foreach (Match m in WarperiaCardRegex.Matches(html))
            {
                string name = WebUtility.HtmlDecode(m.Groups["name"].Value).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                string? author = m.Groups["author"].Success ? WebUtility.HtmlDecode(m.Groups["author"].Value).Trim() : null;
                string desc = WebUtility.HtmlDecode(m.Groups["desc"].Value).Trim();
                string? thumbToken = m.Groups["thumb"].Success ? m.Groups["thumb"].Value : null;

                entries.Add(new MarketplaceAddonEntry(
                    name, Author: author, Description: desc.Length == 0 ? null : desc,
                    DetailUrl: m.Groups["url"].Value, Source: "Warperia",
                    Category: null, Downloads: null, LastUpdated: null, ThumbnailUrl: thumbToken));
            }

            SaveCache(cacheKey, entries);
            return (IReadOnlyList<MarketplaceAddonEntry>)entries;
        }, ct);
    }

    /// <summary>
    /// Resolves a Warperia thumbnail's <c>?wimg_token=...</c> URL to real image bytes — needs the same
    /// cookie-carrying <see cref="HttpClient"/> that fetched the listing page (the token 403s
    /// otherwise, same as #5's download tokens), which is exactly why this reuses the internal
    /// <see cref="Http"/> field rather than accepting a client from the caller. Legacy-WoW thumbnails
    /// need none of this — they're plain unauthenticated URLs, loadable directly via
    /// <c>BitmapImage.UriSource</c> in the UI layer without going through this method at all.
    /// </summary>
    public async Task<byte[]?> FetchThumbnailBytesAsync(string tokenUrl, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(tokenUrl, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch
        {
            return null;
        }
    }

    // Legacy-WoW's real per-addon body (About/Beta builds/Notes/Screenshots, verified against the
    // real fetched Questie page) lives in <div id="ftwp-postcontent">...</div>, immediately followed
    // by a sibling <div id="sidebar-div"> - capturing everything between those two markers isolates
    // it from the surrounding page chrome without needing a full HTML parser.
    private static readonly Regex LegacyWowDetailContentRegex = new(
        @"<div id=""ftwp-postcontent"">(?<body>.*?)</div>\s*</div>\s*<div id=""sidebar-div"">",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Fallback for Legacy-WoW's other page template - "tool/generator" addon pages (e.g. ABHelper)
    // don't use the ftwp-postcontent wrapper at all; their real content sits right after a
    // "print the content" PHP comment, still followed by the same sidebar-div marker. Verified
    // against the real fetched ABHelper page, which has zero ftwp-postcontent matches.
    private static readonly Regex LegacyWowToolPageContentRegex = new(
        @"<!--\s*print the content\s*-->(?<body>.*?)</div>\s*<div id=""sidebar-div"">",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Warperia's description tab: <div class="tab-pane ... " id="descriptionTab"><div class="addon-content ...">BODY</div></div><div class="tab-pane ... " id="imagesTab">
    // The next tab-pane's id marks the end, the same "isolate between two real markers" approach as above.
    private static readonly Regex WarperiaDetailContentRegex = new(
        @"id=""descriptionTab""><div class=""addon-content[^""]*"">(?<body>.*?)</div>\s*</div>\s*<div class=""tab-pane",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Fetches and converts one addon's own detail-page description to Markdown, for the Browse
    /// list's "View details" button (mirrors the Installed list's own details dialog) - this is
    /// separate from <see cref="MarketplaceAddonEntry.Description"/>, which is only the short
    /// one-line blurb shown in the listing row, not the addon's real page content.
    /// </summary>
    public async Task<string> FetchAddonDetailsMarkdownAsync(MarketplaceAddonEntry entry, CancellationToken ct)
    {
        string html = await Http.GetStringAsync(entry.DetailUrl, ct);

        return await Task.Run(() =>
        {
            Match match;
            if (entry.Source == "Warperia")
            {
                match = WarperiaDetailContentRegex.Match(html);
            }
            else
            {
                match = LegacyWowDetailContentRegex.Match(html);
                if (!match.Success)
                {
                    match = LegacyWowToolPageContentRegex.Match(html);
                }
            }

            string body = match.Success
                ? HtmlFragmentToMarkdown(match.Groups["body"].Value)
                : "*No description available - open the page directly to see more.*";

            return $"# {entry.Name}\n\n{body}";
        }, ct);
    }

    // Deliberately not a general-purpose HTML-to-Markdown library - just enough tag handling to
    // render the specific WordPress/Bootstrap patterns both sites' detail pages actually use
    // (headings, paragraphs, lists, links, images, bold/italic), verified against the real fetched
    // Questie pages from both providers. Anything unrecognized is stripped rather than crashing, so
    // a page with an unexpected tag degrades to plain text instead of throwing.
    private static string HtmlFragmentToMarkdown(string html)
    {
        string text = html;
        text = Regex.Replace(text, @"<h[1-6][^>]*>(.*?)</h[1-6]>", "\n#### $1\n", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<li[^>]*>(.*?)</li>", "- $1\n", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<img[^>]*\balt=""([^""]*)""[^>]*\bsrc=""([^""]*)""[^>]*/?>", "![$1]($2)");
        text = Regex.Replace(text, @"<img[^>]*\bsrc=""([^""]*)""[^>]*\balt=""([^""]*)""[^>]*/?>", "![$2]($1)");
        text = Regex.Replace(text, @"<img[^>]*\bsrc=""([^""]*)""[^>]*/?>", "![]($1)");
        text = Regex.Replace(text, @"<a[^>]*\bhref=""([^""]*)""[^>]*>(.*?)</a>", "[$2]($1)", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(strong|b)[^>]*>(.*?)</\1>", "**$2**", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(em|i)[^>]*>(.*?)</\1>", "*$2*", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>", "\n");
        text = Regex.Replace(text, @"<p[^>]*>(.*?)</p>", "\n$1\n", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private bool TryLoadCache(string key, out List<MarketplaceAddonEntry>? entries)
    {
        entries = null;
        string path = Path.Combine(CacheDirectory, $"{key}.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<CacheEnvelope>(json, JsonOptions);
            if (cache is null || DateTimeOffset.UtcNow - cache.FetchedAt > CacheLifetime)
            {
                return false;
            }

            entries = cache.Entries;
            return true;
        }
        catch (Exception ex)
        {
            // Broad, not just JsonException - a locked/permission-denied cache file throws
            // IOException/UnauthorizedAccessException from File.ReadAllText itself, not just from a
            // malformed read; either way, degrading to "refetch" is correct (matches SaveCache's own
            // catch-everything pattern).
            _log.Debug($"Marketplace cache for '{key}' was unreadable, refetching: {ex.Message}");
            return false;
        }
    }

    private void SaveCache(string key, List<MarketplaceAddonEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string path = Path.Combine(CacheDirectory, $"{key}.json");
            var cache = new CacheEnvelope { FetchedAt = DateTimeOffset.UtcNow, Entries = entries };
            File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not save marketplace cache for '{key}': {ex.Message}");
        }
    }

    private sealed class CacheEnvelope
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<MarketplaceAddonEntry> Entries { get; set; } = new();
    }
}
