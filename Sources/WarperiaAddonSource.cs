using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Installs an addon from a warperia.com/addon-vanilla/&lt;slug&gt;/ detail page. The download button
/// has no real href or embedded token anymore (site redesign, confirmed 2026-07-17 against the live
/// site) — resolving it now means an AJAX round-trip mirroring the page's own "Download" button click:
/// POST the addon's numeric ID and the page's per-load WordPress nonce to admin-ajax.php's
/// <c>increment_addon_download</c> action, which hands back a ready-to-use, signed, short-lived
/// <c>https://warperia.com/?wdl_token=...</c> URL in its JSON response — the server does the token
/// construction now, unlike the old flow which decoded a base64 token embedded directly in the page.
/// That token URL isn't decoded any further here: it's requested exactly the way the site's own
/// "Download" button would, redirecting (302) to the real file once the site's own gate accepts it.
/// </summary>
public sealed class WarperiaAddonSource : IAddonSource
{
    private const string AjaxUrl = "https://warperia.com/wp-admin/admin-ajax.php";

    // The main "Download" button's own data-addon-id — the first data-download-trigger anchor on the
    // page, which precedes any alternate-expansion dropdown options further down (same "take the
    // first match" reasoning the old data-w-dl token lookup used).
    private static readonly Regex AddonIdRegex =
        new(@"data-download-trigger=""1""[^>]*?data-addon-id=""(?<id>\d+)""", RegexOptions.Singleline | RegexOptions.Compiled);

    // The per-page WordPress nonce the AJAX download endpoint requires, embedded as a base64 data:
    // URI on the "site-addons-js-extra" inline script tag (WordPress's standard wp_localize_script
    // output) — e.g. var siteAddons={"ajax_url":"...","nonce":"...","download_nonce":"..."}.
    private static readonly Regex DownloadNonceScriptRegex =
        new(@"id=""site-addons-js-extra""\s+src=""data:text/javascript;base64,(?<b64>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex DownloadNonceValueRegex =
        new(@"""download_nonce"":""(?<nonce>[^""]+)""", RegexOptions.Compiled);

    private readonly DirectArchiveAddonSource _archive = new();

    public bool CanHandle(string input)
    {
        const string marker = "warperia.com/addon-vanilla/";
        int index = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && input.TrimEnd('/').Length > index + marker.Length;
    }

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        string tokenUrl = await ResolveTokenUrlAsync(input, http, ct);
        AddonDownload archiveResult = await _archive.DownloadAsync(tokenUrl, http, ct);

        // Same reasoning as LegacyWowAddonSource: keep the detail-page URL as the canonical
        // SourceRef, since the token is single-use/short-lived and has to be re-minted from the page
        // on every future check anyway.
        return archiveResult with
        {
            SuggestedName = ExtractSlugName(input) ?? archiveResult.SuggestedName,
            Kind = AddonSourceKind.Archive,
            SourceRef = input,
        };
    }

    public async Task<string?> GetLatestVersionSignatureAsync(string input, HttpClient http, CancellationToken ct)
    {
        try
        {
            string tokenUrl = await ResolveTokenUrlAsync(input, http, ct);
            return await _archive.GetLatestVersionSignatureAsync(tokenUrl, http, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the detail page, extracts the addon ID and per-page download nonce, then calls the
    /// site's own AJAX download endpoint exactly as its "Download" button's click handler does —
    /// returning the ready-to-use token URL from the JSON response. Verified end-to-end against the
    /// live site 2026-07-17.
    /// </summary>
    private static async Task<string> ResolveTokenUrlAsync(string input, HttpClient http, CancellationToken ct)
    {
        string html = await http.GetStringAsync(input, ct);

        Match idMatch = AddonIdRegex.Match(html);
        if (!idMatch.Success)
        {
            throw new InvalidOperationException(
                $"Could not find a download button on {input} — the page structure may have changed, or this addon has no configured download.");
        }

        Match scriptMatch = DownloadNonceScriptRegex.Match(html);
        if (!scriptMatch.Success)
        {
            throw new InvalidOperationException(
                $"Could not find the download nonce on {input} — the page structure may have changed.");
        }

        string decodedScript;
        try
        {
            decodedScript = Encoding.UTF8.GetString(Convert.FromBase64String(scriptMatch.Groups["b64"].Value));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Could not decode the download nonce script on {input}.", ex);
        }

        Match nonceMatch = DownloadNonceValueRegex.Match(decodedScript);
        if (!nonceMatch.Success)
        {
            throw new InvalidOperationException($"Could not find a download nonce value on {input}.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AjaxUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "increment_addon_download",
                ["addon_id"] = idMatch.Groups["id"].Value,
                ["nonce"] = nonceMatch.Groups["nonce"].Value,
            }),
        };
        request.Headers.Referrer = new Uri(input);

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);

        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("success", out JsonElement successEl) || !successEl.GetBoolean() ||
            !doc.RootElement.TryGetProperty("data", out JsonElement dataEl) ||
            !dataEl.TryGetProperty("download_url", out JsonElement urlEl) ||
            urlEl.GetString() is not { Length: > 0 } tokenUrl)
        {
            throw new InvalidOperationException($"Warperia's download endpoint did not return a download URL for {input}.");
        }

        return tokenUrl;
    }

    private static string? ExtractSlugName(string input)
    {
        string trimmed = input.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == trimmed.Length - 1)
        {
            return null;
        }

        string slug = trimmed[(lastSlash + 1)..];
        string[] words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? null : string.Join(' ', Array.ConvertAll(words, w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
