using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Installs an addon from a legacy-wow.com/vanilla-addons/&lt;slug&gt;/ detail page. The site has no
/// API — the real download URL isn't a plain href, it's passed as the first argument to an inline
/// <c>onclick="updateC('&lt;url&gt;', '&lt;postId&gt;'); return false;"</c> handler (the second argument
/// is just a download-counter post ID). No JavaScript execution is needed to get at it: the literal
/// URL is already sitting in the raw HTML attribute, just not in the href. That resolved URL is
/// usually a GitHub archive zip or a legacy-wow.com-hosted file — either way it's a plain,
/// unauthenticated static download, so the actual fetch/extract is delegated entirely to
/// <see cref="DirectArchiveAddonSource"/> once the real URL is known.
/// </summary>
public sealed class LegacyWowAddonSource : IAddonSource
{
    private static readonly Regex DownloadUrlRegex =
        new(@"onclick\s*=\s*[""']updateC\(\s*['""](?<url>[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DirectArchiveAddonSource _archive = new();

    public bool CanHandle(string input)
    {
        const string marker = "legacy-wow.com/vanilla-addons/";
        int index = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && input.TrimEnd('/').Length > index + marker.Length;
    }

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        string resolvedUrl = await ResolveDownloadUrlAsync(input, http, ct);
        AddonDownload archiveResult = await _archive.DownloadAsync(resolvedUrl, http, ct);

        // Keep the original detail-page URL as the canonical SourceRef, not the resolved file URL —
        // the resolved file can change (a new upload, a different filename) whenever the addon is
        // updated, so re-resolving from the detail page has to happen fresh on every future check.
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
            string resolvedUrl = await ResolveDownloadUrlAsync(input, http, ct);
            return await _archive.GetLatestVersionSignatureAsync(resolvedUrl, http, ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ResolveDownloadUrlAsync(string input, HttpClient http, CancellationToken ct)
    {
        string html = await http.GetStringAsync(input, ct);
        Match match = DownloadUrlRegex.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find a download link on {input} — the page structure may have changed, or this addon has no configured download.");
        }

        string url = WebUtility.HtmlDecode(match.Groups["url"].Value);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Download link found on {input} isn't a valid absolute URL: {url}");
        }

        return url;
    }

    /// <summary>Best-effort display name from the page's own URL slug (e.g. "auctioneer" -&gt; "Auctioneer") — only ever used as a fallback until the addon's own .toc title takes over.</summary>
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
