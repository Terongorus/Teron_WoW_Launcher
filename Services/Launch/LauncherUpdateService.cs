using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Launch;

/// <summary>A newer stable release than the one currently running, resolved from GitHub.</summary>
public sealed record LauncherUpdateInfo(string Version, string DownloadUrl, string Sha256);

/// <summary>
/// Checks GitHub for a newer stable release of the launcher itself (not the game client, not an
/// addon) and downloads its installer. Hardcoded to this one specific repo, matching the existing
/// ChangelogUrl/ReadmeUrl precedent in MainWindow — this app is never pointed at anything else.
/// </summary>
public sealed class LauncherUpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/Terongorus/Teron_WoW_Launcher/releases/latest";
    private const string InstallerAssetName = "TeronWoWLauncherSetup-x86.exe";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Logger _log = Logger.Instance;

    private static HttpClient CreateHttpClient()
    {
        // GitHub's API rejects requests without a User-Agent. No fixed timeout - this client also
        // does the installer download, unlike MainWindow's short-timeout doc-fetch client.
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeronWoWLauncher");
        return http;
    }

    /// <summary>
    /// GitHub's own /releases/latest semantics already exclude prerelease and draft releases
    /// server-side - no manual filtering needed. Returns null if the request fails, the installer
    /// asset isn't attached, or the release isn't actually newer than <paramref name="currentVersion"/>.
    /// </summary>
    public async Task<LauncherUpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken ct)
    {
        using HttpResponseMessage resp = await Http.GetAsync(ReleasesApiUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Debug($"Launcher update check: GitHub returned {(int)resp.StatusCode}.");
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        string? tagName = root.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        string latestVersion = tagName.StartsWith('v') ? tagName[1..] : tagName;
        if (!IsNewer(latestVersion, currentVersion))
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out JsonElement assets))
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
            if (!string.Equals(name, InstallerAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
            string? digest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(digest))
            {
                return null;
            }

            const string sha256Prefix = "sha256:";
            string sha256 = digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase)
                ? digest[sha256Prefix.Length..]
                : digest;

            return new LauncherUpdateInfo(latestVersion, url, sha256);
        }

        return null;
    }

    /// <summary>
    /// Downloads the installer to a temp file and verifies its SHA-256 against the release asset's
    /// own digest before returning — throws rather than handing back a maybe-corrupted file, since
    /// the caller launches whatever this returns with admin rights. progress reuses GameInstallService's
    /// InstallProgress shape (Extracting always false here - this flow has no extraction phase) so it
    /// can be wired through the same MainProgressBar the client download already uses (issue #13).
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string downloadUrl, string expectedSha256, CancellationToken ct, IProgress<InstallProgress>? progress = null)
    {
        // Every previous update leaves its installer + containing folder behind under %TEMP% forever
        // (the installer can't be deleted before/while it runs, and the app closes right after
        // starting it, so nothing else ever gets a chance to clean it up) - by the time a NEW update
        // check runs, any prior installer has long since finished, so it's always safe to sweep old
        // leftovers here rather than let them accumulate indefinitely.
        CleanupStaleInstallerFolders();

        // The GUID goes on the containing folder, not the file itself — the exe keeps its real,
        // recognizable name (matching what Inno Setup's own installer identity shows), since a
        // random-looking filename on the UAC elevation prompt reads as suspicious to the user.
        string tempDir = Path.Combine(Path.GetTempPath(), $"teronwow_update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string installerPath = Path.Combine(tempDir, InstallerAssetName);

        using (HttpResponseMessage resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using Stream httpStream = await resp.Content.ReadAsStreamAsync(ct);
            await using FileStream fs = File.Create(installerPath);

            byte[] buffer = new byte[81920];
            long readTotal = 0;
            long lastReported = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;

                if (stopwatch.Elapsed.TotalMilliseconds >= 250)
                {
                    double speed = (readTotal - lastReported) / stopwatch.Elapsed.TotalSeconds;
                    progress?.Report(new InstallProgress(readTotal, total, speed));
                    lastReported = readTotal;
                    stopwatch.Restart();
                }
            }

            progress?.Report(new InstallProgress(readTotal, total, 0));
        }

        string actualSha256;
        await using (FileStream fs = File.OpenRead(installerPath))
        {
            byte[] hash = await SHA256.HashDataAsync(fs, ct);
            actualSha256 = Convert.ToHexStringLower(hash);
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(installerPath); } catch { /* best-effort cleanup */ }
            throw new InvalidOperationException(
                "The downloaded installer failed its integrity check (SHA-256 mismatch) and was discarded. Please try again.");
        }

        return installerPath;
    }

    /// <summary>Best-effort: one locked/in-use leftover folder must not stop the rest from being
    /// swept, and must never block the actual download this precedes.</summary>
    private void CleanupStaleInstallerFolders()
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(Path.GetTempPath(), "teronwow_update_*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _log.Debug($"Could not remove stale update folder {dir}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not scan for stale update folders: {ex.Message}");
        }
    }

    private static bool IsNewer(string latestVersion, string currentVersion)
    {
        // GitHub's /releases/latest excludes prereleases server-side, so latestVersion is always a
        // plain "X.Y.Z". currentVersion can carry a "-beta.N"-style suffix (AppInfo.Version reflects
        // <Version> verbatim) which System.Version.TryParse rejects outright - strip it before parsing
        // so a beta build's own update check still works instead of silently never firing.
        static string StripSuffix(string v)
        {
            int dash = v.IndexOf('-', StringComparison.Ordinal);
            return dash >= 0 ? v[..dash] : v;
        }

        if (!Version.TryParse(StripSuffix(latestVersion), out Version? latest) ||
            !Version.TryParse(StripSuffix(currentVersion), out Version? current))
        {
            return false;
        }

        return latest > current;
    }
}
