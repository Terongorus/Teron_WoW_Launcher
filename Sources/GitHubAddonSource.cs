using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Installs an addon from a GitHub repository URL — the primary source for 1.12.1 addons, since the
/// ecosystem is GitHub-centric with no central API. Prefers the latest release's .zip asset; falls
/// back to a source zip of the default branch. Uses the public GitHub REST API (no auth needed).
/// </summary>
public sealed class GitHubAddonSource : IAddonSource
{
    private static readonly Regex RepoRegex =
        new(@"github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Logger _log = Logger.Instance;

    public bool CanHandle(string input)
        => input.Contains("github.com", StringComparison.OrdinalIgnoreCase) && RepoRegex.IsMatch(input);

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        Match m = RepoRegex.Match(input);
        string owner = m.Groups["owner"].Value;
        string repo = m.Groups["repo"].Value.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);

        string? downloadUrl = null;
        string version = string.Empty;

        // 1) Latest release with a .zip asset.
        try
        {
            using HttpResponseMessage rel =
                await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct);
            if (rel.IsSuccessStatusCode)
            {
                using JsonDocument doc = JsonDocument.Parse(await rel.Content.ReadAsStringAsync(ct));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("tag_name", out JsonElement tag))
                {
                    version = tag.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("assets", out JsonElement assets))
                {
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? string.Empty;
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"GitHub releases lookup failed for {owner}/{repo}: {ex.Message}");
        }

        // 2) Fall back to the default branch source zip.
        if (downloadUrl is null)
        {
            string branch = "master";
            try
            {
                using HttpResponseMessage repoResp =
                    await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}", ct);
                if (repoResp.IsSuccessStatusCode)
                {
                    using JsonDocument doc = JsonDocument.Parse(await repoResp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("default_branch", out JsonElement db))
                    {
                        branch = db.GetString() ?? "master";
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"GitHub repo lookup failed for {owner}/{repo}: {ex.Message}");
            }

            downloadUrl = $"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{branch}";
            if (string.IsNullOrEmpty(version))
            {
                version = $"{branch}@{DateTime.UtcNow:yyyy-MM-dd}";
            }
        }

        string temp = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}.zip");
        using (HttpResponseMessage resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(temp);
            await resp.Content.CopyToAsync(fs, ct);
        }

        return new AddonDownload(temp, repo, version, AddonSourceKind.GitHub, $"https://github.com/{owner}/{repo}");
    }
}
