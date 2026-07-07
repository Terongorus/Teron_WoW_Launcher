using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace TeronWoWLauncher.Services;

/// <summary>Snapshot of download/extract progress for the UI.</summary>
public sealed record InstallProgress(long Downloaded, long Total, double BytesPerSecond, bool Extracting = false);

/// <summary>Result of comparing the remote client zip against what was last installed.</summary>
public sealed record UpdateCheckResult(bool UpdateAvailable, string? RemoteSignature, string Detail);

/// <summary>
/// Downloads and installs the vanilla 1.12.1 client. Our own implementation of the "download one zip
/// and extract it" approach: the whole base client is a single ZIP at a configurable URL, so this
/// works for any vanilla server that hosts a client zip, not just one. Supports resuming an
/// interrupted download via HTTP Range requests.
/// </summary>
public sealed class GameInstallService
{
    /// <summary>Built-in default source (the vanilla client zip TwinStar hosts).</summary>
    public const string DefaultClientUrl = "http://cdn.twinstar-wow.com/WoW_Vanilla.zip";

    /// <summary>Expected client version for a correct vanilla install.</summary>
    public const string ExpectedVersion = "1.12.1";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Logger _log = Logger.Instance;

    public bool IsInstalled(string installDir) => File.Exists(Path.Combine(installDir, "WoW.exe"));

    /// <summary>The detected client version (e.g. "1.12.1"), or null if WoW.exe is absent.</summary>
    public string? GetClientVersion(string installDir)
    {
        string exe = Path.Combine(installDir, "WoW.exe");
        if (!File.Exists(exe))
        {
            return null;
        }

        FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
        return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}";
    }

    /// <summary>True when a correct 1.12.1 client is already installed.</summary>
    public bool IsUpToDate(string installDir) => GetClientVersion(installDir) == ExpectedVersion;

    /// <summary>
    /// Compares the remote client zip's size/Last-Modified/ETag against <paramref name="installedSignature"/>
    /// (recorded at the time of the last successful install). There's no per-file manifest for the
    /// single-zip vanilla client, so this is a whole-archive freshness check, not a per-file diff —
    /// <see cref="UpdateCheckResult.Detail"/> says so plainly for the confirmation dialog.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string url, string? installedSignature, CancellationToken ct)
    {
        (string? signature, long length, string? modified) = await GetRemoteSignatureAsync(url, ct);
        if (signature is null)
        {
            return new UpdateCheckResult(false, null, "Could not reach the client download URL to check for updates.");
        }

        if (installedSignature is null)
        {
            return new UpdateCheckResult(false, signature, "No update information recorded for the current install.");
        }

        bool changed = signature != installedSignature;
        string detail = changed
            ? $"The client archive at the source URL has changed (size: {length:N0} bytes" +
              (modified is null ? ")" : $", last modified: {modified})") +
              ". There is no per-file update list for this client — updating re-downloads and " +
              "re-extracts the full archive, overwriting any local file it contains a different version of."
            : "The installed client matches the source archive.";

        return new UpdateCheckResult(changed, signature, detail);
    }

    /// <summary>A signature capturing the remote zip's identity: "<c>length|etag-or-last-modified</c>".</summary>
    private async Task<(string? Signature, long Length, string? Modified)> GetRemoteSignatureAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (null, 0, null);
            }

            long length = response.Content.Headers.ContentLength ?? 0;
            string? modified = response.Headers.ETag?.Tag
                ?? response.Content.Headers.LastModified?.ToString("O");

            return ($"{length}|{modified}", length, modified);
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not fetch remote client signature: {ex.Message}");
            return (null, 0, null);
        }
    }

    /// <summary>
    /// Download the client zip (resuming a prior partial download when possible) and extract it into
    /// <paramref name="installDir"/>. If a single top-level folder wraps the contents, it is stripped.
    /// Returns the remote signature at the time of install, to be stored for future update checks.
    /// </summary>
    public async Task<string?> DownloadAndInstallAsync(
        string url, string installDir, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(installDir);
        string tempZip = Path.Combine(Path.GetTempPath(), "TeronWoW_client.zip.part");

        (string? signature, long total, _) = await GetRemoteSignatureAsync(url, ct);
        long existing = File.Exists(tempZip) ? new FileInfo(tempZip).Length : 0;
        if (total > 0 && existing > total)
        {
            File.Delete(tempZip);
            existing = 0;
        }

        bool complete = total > 0 && existing == total;
        if (!complete)
        {
            await DownloadAsync(url, tempZip, existing, total, progress, ct);
        }

        _log.Info("Download complete; extracting client...");
        progress?.Report(new InstallProgress(total, total, 0, Extracting: true));
        ExtractZip(tempZip, installDir);

        try { File.Delete(tempZip); } catch { /* leave the cache if it can't be removed */ }
        _log.Info($"Client installed to {installDir}.");
        return signature;
    }

    private async Task DownloadAsync(
        string url, string tempZip, long existing, long total, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using HttpResponseMessage response =
            await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // If the server ignored our Range request, restart from the beginning.
        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            existing = 0;
            if (File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }
        }

        response.EnsureSuccessStatusCode();

        long reportTotal = total > 0 ? total : (response.Content.Headers.ContentLength ?? 0) + existing;

        await using Stream httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            tempZip, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[81920];
        long readTotal = existing;
        long lastReported = existing;
        var stopwatch = Stopwatch.StartNew();

        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;

            if (stopwatch.Elapsed.TotalMilliseconds >= 250)
            {
                double speed = (readTotal - lastReported) / stopwatch.Elapsed.TotalSeconds;
                progress?.Report(new InstallProgress(readTotal, reportTotal, speed));
                lastReported = readTotal;
                stopwatch.Restart();
            }
        }

        progress?.Report(new InstallProgress(readTotal, reportTotal, 0));
    }

    private static void ExtractZip(string zipPath, string destDir)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);

        // Strip a single wrapping top-level folder if the whole archive is nested in one.
        var topLevels = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();
        string strip = topLevels.Count == 1 ? topLevels[0] + "/" : string.Empty;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue; // directory entry
            }

            string rel = entry.FullName.Replace('\\', '/');
            if (strip.Length > 0 && rel.StartsWith(strip, StringComparison.Ordinal))
            {
                rel = rel[strip.Length..];
            }

            if (string.IsNullOrEmpty(rel))
            {
                continue;
            }

            string dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}
