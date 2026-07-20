using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Launch;

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

    // Fixed name (not a per-attempt GUID) is what makes resuming a partial download possible at
    // all - the next DownloadAndInstallAsync call finds the same file and Range-requests the rest.
    private static readonly string TempZipPath = Path.Combine(Path.GetTempPath(), "TeronWoW_client.zip.part");

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Logger _log = Logger.Instance;

    // Records exactly which top-level entries the last successful install/repair placed in
    // installDir, so "Delete Game Files" can remove precisely what the client archive itself
    // provided (Data\, WoW.exe, Fonts\, etc.) without touching anything else living alongside it
    // (Interface\AddOns, WTF\, Logs\, realmlist.wtf, the launcher's own tracked state) - and without
    // needing to re-fetch/re-parse the remote archive just to find out what it would contain.
    private const string InstallManifestFileName = ".teronwow-install-manifest.txt";

    public bool IsInstalled(string installDir) => File.Exists(Path.Combine(installDir, "WoW.exe"));

    /// <summary>Whether a manifest from a prior install/repair exists, i.e. whether DeleteInstalledClientFiles knows what it's allowed to remove.</summary>
    public bool HasInstallManifest(string installDir) => File.Exists(Path.Combine(installDir, InstallManifestFileName));

    /// <summary>
    /// Deletes exactly the top-level files/folders recorded by the last successful install/repair,
    /// then removes the manifest itself. Throws <see cref="InvalidOperationException"/> if no
    /// manifest exists (e.g. a client installed by a launcher version before this existed, or copied
    /// in manually) — deleting is a destructive, one-way action, so this refuses to guess at a
    /// hardcoded file list rather than risk removing (or failing to remove) the wrong things.
    /// </summary>
    public void DeleteInstalledClientFiles(string installDir)
    {
        string manifestPath = Path.Combine(installDir, InstallManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "No install record found for this folder — run Repair once first so future deletes know exactly what to remove.");
        }

        foreach (string entry in File.ReadAllLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            string path = Path.Combine(installDir, entry);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        File.Delete(manifestPath);
    }

    /// <summary>The detected client version (e.g. "1.12.1"), or null if WoW.exe is absent or its
    /// version resource can't be read (corrupt/zero-byte/mid-write/locked-by-AV file) - either way
    /// treated the same as "not a verified install," which correctly routes the caller to
    /// (re)download rather than blindly adopting a broken file in place.</summary>
    public string? GetClientVersion(string installDir)
    {
        string exe = Path.Combine(installDir, "WoW.exe");
        if (!File.Exists(exe))
        {
            return null;
        }

        try
        {
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
            return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}";
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read WoW.exe's version info at {exe}: {ex.Message}");
            return null;
        }
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
        string tempZip = TempZipPath;

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

        // Off the calling thread deliberately - ExtractZip is a plain synchronous file-I/O loop with
        // no await in it anywhere, so run on the UI thread (as the download loop's own real awaits
        // never do) it would block the whole UI - including the Dispatcher-marshalled progress reports
        // below, which would then queue up unable to actually paint until extraction finished. Task.Run
        // moves the blocking work off the UI thread; IProgress<T>.Report still safely marshals each
        // report back to it as it comes in from here.
        IReadOnlyList<string> topLevelEntries = await Task.Run(() => ExtractZip(tempZip, installDir, progress), ct);
        File.WriteAllLines(Path.Combine(installDir, InstallManifestFileName), topLevelEntries);

        try { File.Delete(tempZip); } catch { /* leave the cache if it can't be removed */ }
        _log.Info($"Client installed to {installDir}.");
        return signature;
    }

    /// <summary>
    /// Deletes the partial download left behind by a cancelled DownloadAndInstallAsync, so a future
    /// attempt starts completely fresh instead of resuming stale bytes - the distinction between a
    /// hard Cancel and a Pause (which deliberately leaves this file alone so Resume can pick back up).
    /// </summary>
    public void DeletePartialDownload()
    {
        try { File.Delete(TempZipPath); } catch { /* best-effort */ }
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

    /// <summary>Extracts the archive into destDir and returns the top-level names it actually wrote there (post-strip) - i.e. what DeleteInstalledClientFiles is later allowed to remove.</summary>
    private static IReadOnlyList<string> ExtractZip(string zipPath, string destDir, IProgress<InstallProgress>? progress)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);

        // Uncompressed size (ZipArchiveEntry.Length) rather than entry count - a client zip's few huge
        // MPQ-style data files dwarf its many small ones, so "entries done" would sit near 100% while
        // the actual big files are still being written. Reported the same throttled way as the
        // download loop, not the previous single "Extracting=true, then nothing for ~15s" report,
        // which read as the launcher having stalled.
        long totalBytes = archive.Entries.Sum(e => e.Length);
        long extractedBytes = 0;
        long lastReported = 0;
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new InstallProgress(0, totalBytes, 0, Extracting: true));

        // Strip a single wrapping top-level folder if the whole archive is nested in one.
        var topLevels = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();
        string strip = topLevels.Count == 1 ? topLevels[0] + "/" : string.Empty;

        // The names actually written under destDir - if the whole archive was nested in one
        // wrapping folder (stripped above), that wrapper itself doesn't exist in destDir, only
        // whatever was inside it does; recompute post-strip so the manifest matches reality.
        var writtenTopLevels = strip.Length > 0
            ? archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && e.FullName.Replace('\\', '/').StartsWith(strip, StringComparison.Ordinal))
                .Select(e => e.FullName.Replace('\\', '/')[strip.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList()
            : topLevels;

        // destDirFull always ends with a separator so the StartsWith prefix check below can't be
        // fooled by a sibling folder that merely shares destDir as a string prefix (e.g. "...\Wow" vs
        // a malicious entry resolving to "...\WowEvil").
        string destDirFull = Path.GetFullPath(destDir);
        if (!destDirFull.EndsWith(Path.DirectorySeparatorChar))
        {
            destDirFull += Path.DirectorySeparatorChar;
        }

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

            // Zip Slip guard: a malicious or corrupted archive could contain an entry like
            // "../../../Windows/System32/evil.dll" that would otherwise extract outside destDir
            // entirely. Path.Combine doesn't normalize ".." segments away, so the check has to happen
            // after resolving the full path, not on the raw entry name.
            string destFull = Path.GetFullPath(dest);
            if (!destFull.StartsWith(destDirFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to extract '{entry.FullName}' — its path resolves outside the install folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            entry.ExtractToFile(destFull, overwrite: true);

            extractedBytes += entry.Length;
            if (stopwatch.Elapsed.TotalMilliseconds >= 250)
            {
                double speed = (extractedBytes - lastReported) / stopwatch.Elapsed.TotalSeconds;
                progress?.Report(new InstallProgress(extractedBytes, totalBytes, speed, Extracting: true));
                lastReported = extractedBytes;
                stopwatch.Restart();
            }
        }

        progress?.Report(new InstallProgress(extractedBytes, totalBytes, 0, Extracting: true));

        return writtenTopLevels!;
    }
}
