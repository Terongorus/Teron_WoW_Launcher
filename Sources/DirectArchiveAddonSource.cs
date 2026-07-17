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
            Directory.CreateDirectory(contentDir); // SharpCompress requires the destination to already exist

            // Off the calling thread deliberately - archive extraction is synchronous SharpCompress
            // I/O with no await in it, and DownloadAsync is always awaited directly from the UI thread
            // (AddonLibrary.AddAsync, itself called from MainWindow's UI thread) - a large archive
            // would otherwise noticeably freeze the window, the same bug class already fixed for the
            // client zip's own extraction.
            await Task.Run(() => ExtractArchive(archivePath, contentDir), ct);

            string name = Path.GetFileNameWithoutExtension(input);
            return new AddonDownload(contentDir, name, signature, AddonSourceKind.Archive, input);
        }
        finally
        {
            try { File.Delete(archivePath); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Extracts every entry individually (rather than the ArchiveFactory.WriteToDirectory convenience
    /// method) specifically so each one can be checked against Zip Slip - a malicious or corrupted
    /// archive entry like "../../../Windows/System32/evil.dll" would otherwise extract outside
    /// contentDir. This matters more here than for the launcher's own client zip: an addon archive is
    /// arbitrary third-party content (GitHub/Warperia/LegacyWoW/a raw URL/a local file the user
    /// picked), not a single hardcoded trusted source.
    /// </summary>
    private static void ExtractArchive(string archivePath, string contentDir)
    {
        string contentDirFull = Path.GetFullPath(contentDir);
        if (!contentDirFull.EndsWith(Path.DirectorySeparatorChar))
        {
            contentDirFull += Path.DirectorySeparatorChar;
        }

        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.Key is null)
            {
                continue;
            }

            string destFull = Path.GetFullPath(Path.Combine(contentDir, entry.Key));
            if (!destFull.StartsWith(contentDirFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to extract '{entry.Key}' — its path resolves outside the destination folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            entry.WriteToFile(destFull, new ExtractionOptions { Overwrite = true });
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
