using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Fallback source: a local .zip file the user already downloaded, or a direct .zip URL. This is the
/// "open the archive in the manager and it installs the addon" path. Only .zip is supported natively
/// (.rar/.7z would need an extra library).
/// </summary>
public sealed class DirectArchiveAddonSource : IAddonSource
{
    public bool CanHandle(string input) => true; // last in the resolver chain

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}.zip");

        if (File.Exists(input))
        {
            File.Copy(input, temp, overwrite: true);
        }
        else
        {
            using HttpResponseMessage resp = await http.GetAsync(input, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(temp);
            await resp.Content.CopyToAsync(fs, ct);
        }

        string name = Path.GetFileNameWithoutExtension(input);
        return new AddonDownload(temp, name, DateTime.UtcNow.ToString("yyyy-MM-dd"), AddonSourceKind.Archive, input);
    }
}
