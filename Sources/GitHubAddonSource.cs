using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // GuessedBranch is set only when the default-branch lookup below didn't come back (most
    // commonly GitHub's unauthenticated REST API rate limit — 60/hour — already used up earlier in
    // the same session installing other addons), so DownloadAsync knows it's worth retrying with
    // the other common branch name if this guess turns out wrong.
    //
    // HighConfidence marks whether Signature is trustworthy enough to compare against a
    // previously-stored one for update checking. A release tag is always trustworthy. A branch
    // signature only is when BOTH the default-branch and the branch-sha lookups succeeded — if
    // either one was skipped/rate-limited, the resulting signature is missing its real commit sha
    // (or the branch itself was guessed), so comparing it against an earlier high-confidence
    // signature would near-always "mismatch" and falsely claim an update is available even when
    // nothing actually changed. GetLatestVersionSignatureAsync returns null instead in that case,
    // so the caller just skips this check cycle rather than acting on an unreliable comparison.
    private sealed record Resolved(string DownloadUrl, string Signature, string? GuessedBranch = null, bool HighConfidence = true);

    /// <summary>
    /// Figures out where to download from and what signature identifies "this version" — shared by
    /// <see cref="DownloadAsync"/> and <see cref="GetLatestVersionSignatureAsync"/> so both agree.
    /// </summary>
    private async Task<Resolved> ResolveAsync(string owner, string repo, HttpClient http, CancellationToken ct)
    {
        // 1) Latest release with a .zip asset.
        string? downloadUrl = null;
        string tag = string.Empty;
        try
        {
            using HttpResponseMessage rel =
                await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct);
            if (rel.IsSuccessStatusCode)
            {
                using JsonDocument doc = JsonDocument.Parse(await rel.Content.ReadAsStringAsync(ct));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("tag_name", out JsonElement tagEl))
                {
                    tag = tagEl.GetString() ?? string.Empty;
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

        if (downloadUrl is not null)
        {
            return new Resolved(downloadUrl, $"release:{tag}");
        }

        // 2) Fall back to the default branch's source zip.
        string branch = "master";
        bool branchConfirmed = false;
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
                    branchConfirmed = true;
                }
            }
            else if (IsRateLimited(repoResp))
            {
                _log.Warn($"GitHub API rate limit reached while resolving '{owner}/{repo}''s default branch; guessing '{branch}' for now.");
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"GitHub repo lookup failed for {owner}/{repo}: {ex.Message}");
        }

        string sha = branch;
        bool shaConfirmed = false;
        if (branchConfirmed)
        {
            try
            {
                using HttpResponseMessage branchResp =
                    await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/branches/{branch}", ct);
                if (branchResp.IsSuccessStatusCode)
                {
                    using JsonDocument doc = JsonDocument.Parse(await branchResp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("commit", out JsonElement commit) &&
                        commit.TryGetProperty("sha", out JsonElement shaEl))
                    {
                        sha = shaEl.GetString() ?? branch;
                        shaConfirmed = true;
                    }
                }
                else if (IsRateLimited(branchResp))
                {
                    _log.Warn($"GitHub API rate limit reached while resolving '{owner}/{repo}''s '{branch}' commit; update checks will be skipped until it clears.");
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"GitHub branch lookup failed for {owner}/{repo}/{branch}: {ex.Message}");
            }
        }

        string url = $"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{branch}";
        string signature = branchConfirmed ? $"branch:{branch}@{sha}" : $"branch:{branch}";
        return new Resolved(url, signature, GuessedBranch: branchConfirmed ? null : branch, HighConfidence: branchConfirmed && shaConfirmed);
    }

    private static bool IsRateLimited(HttpResponseMessage resp)
        => resp.StatusCode == System.Net.HttpStatusCode.Forbidden
           && resp.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values)
           && values.Contains("0");

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        Match m = RepoRegex.Match(input);
        string owner = m.Groups["owner"].Value;
        string repo = m.Groups["repo"].Value.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);

        Resolved resolved = await ResolveAsync(owner, repo, http, ct);

        string temp = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}.zip");
        HttpResponseMessage resp = await http.GetAsync(resolved.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

        // GuessedBranch means the default-branch API lookup didn't come back, so "master" was a
        // blind guess — "main" is the only other branch name worth trying before giving up.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && resolved.GuessedBranch is string guessed)
        {
            string alt = string.Equals(guessed, "master", StringComparison.OrdinalIgnoreCase) ? "main" : "master";
            _log.Warn($"'{owner}/{repo}': guessed branch '{guessed}' doesn't exist, retrying with '{alt}'.");
            resp.Dispose();
            string altUrl = $"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{alt}";
            resp = await http.GetAsync(altUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resolved = resolved with { Signature = $"branch:{alt}" };
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(temp);
            await resp.Content.CopyToAsync(fs, ct);
        }

        return new AddonDownload(temp, repo, resolved.Signature, AddonSourceKind.GitHub, $"https://github.com/{owner}/{repo}");
    }

    public async Task<string?> GetLatestVersionSignatureAsync(string input, HttpClient http, CancellationToken ct)
    {
        Match m = RepoRegex.Match(input);
        if (!m.Success)
        {
            return null;
        }

        string owner = m.Groups["owner"].Value;
        string repo = m.Groups["repo"].Value.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);

        try
        {
            Resolved resolved = await ResolveAsync(owner, repo, http, ct);
            return resolved.HighConfidence ? resolved.Signature : null;
        }
        catch (Exception ex)
        {
            _log.Debug($"GitHub update check failed for {owner}/{repo}: {ex.Message}");
            return null;
        }
    }
}
