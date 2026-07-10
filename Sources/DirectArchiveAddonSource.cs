using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Fallback source: a local archive file the user already downloaded, or a direct archive URL.
/// Extraction supports .zip/.rar/.7z via SharpCompress (the BCL only handles .zip).
/// </summary>
public sealed class DirectArchiveAddonSource : IAddonSource
{
    public bool CanHandle(string input) => true; // last in the resolver chain

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}{Path.GetExtension(input)}");
        string? signature;

        try
        {
            if (File.Exists(input))
            {
                File.Copy(input, archivePath, overwrite: true);
                signature = null; // a local file has no "remote" to compare against later
            }
            else
            {
                signature = await GetLatestVersionSignatureAsync(input, http, ct);
                using HttpResponseMessage resp = await http.GetAsync(input, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using FileStream fs = File.Create(archivePath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            string contentDir = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}");
            ArchiveFactory.WriteToDirectory(archivePath, contentDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });

            string name = Path.GetFileNameWithoutExtension(input);
            return new AddonDownload(contentDir, name, signature, AddonSourceKind.Archive, input);
        }
        finally
        {
            try { File.Delete(archivePath); } catch { /* temp cleanup best-effort */ }
        }
    }

    public async Task<string?> GetLatestVersionSignatureAsync(string input, HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null; // a local file path has nothing remote to check
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, input);
            using HttpResponseMessage resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            long length = resp.Content.Headers.ContentLength ?? 0;
            string? modified = resp.Headers.ETag?.Tag ?? resp.Content.Headers.LastModified?.ToString("O");
            return $"{length}|{modified}";
        }
        catch
        {
            return null;
        }
    }
}
