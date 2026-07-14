using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Installs an addon from a warperia.com/addon-vanilla/&lt;slug&gt;/ detail page. The download button
/// has no real href — the actual target is base64-encoded in the anchor's <c>data-w-dl</c> attribute,
/// decoding to a signed, short-lived, IP/session-locked <c>https://warperia.com/?wdl_token=...</c>
/// URL. That token URL isn't decoded any further here: it's requested exactly the way the site's own
/// "Download" button would, redirecting (302) to the real file once the site's own gate accepts it.
/// This only works if the token request reuses the *same* cookie-carrying <see cref="HttpClient"/>
/// that fetched the detail page a moment earlier (verified empirically — the token's IP/session check
/// otherwise 403s), so the two requests are always made back-to-back through the same client.
/// </summary>
public sealed class WarperiaAddonSource : IAddonSource
{
    private static readonly Regex DownloadTokenRegex =
        new(@"data-w-dl\s*=\s*""(?<token>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// Fetches the detail page and decodes the first <c>data-w-dl</c> token found — the page's own
    /// main "Download" button, which precedes any alternate-mirror/expansion options further down.
    /// </summary>
    private static async Task<string> ResolveTokenUrlAsync(string input, HttpClient http, CancellationToken ct)
    {
        string html = await http.GetStringAsync(input, ct);
        Match match = DownloadTokenRegex.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find a download button on {input} — the page structure may have changed, or this addon has no configured download.");
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(match.Groups["token"].Value);
            string tokenUrl = Encoding.UTF8.GetString(bytes);
            if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new FormatException($"Decoded token isn't a valid absolute URL: {tokenUrl}");
            }

            return tokenUrl;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Download token on {input} could not be decoded — the page's download mechanism may have changed.", ex);
        }
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
