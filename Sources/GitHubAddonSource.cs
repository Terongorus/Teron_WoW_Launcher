using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Installs an addon from a GitHub repository URL via a real git clone — the primary source for
/// 1.12.1 addons, since the ecosystem is GitHub-centric with no central API. Each tracked repo gets
/// a persistent local clone under <see cref="AddonPaths.AddonRepoCacheDir"/>, kept across app runs
/// so update checks and pulls are incremental instead of a fresh download every time.
///
/// This deliberately avoids the api.github.com REST API entirely: it has an unauthenticated 60/hour
/// rate limit that a handful of tracked addons can exhaust in one Refresh, and previously caused
/// both false "no update" (a stale release tag hiding real commits pushed straight to the branch)
/// and false "update available" results (a rate-limited resolution silently corrupting the stored
/// baseline). Git's own smart-HTTP protocol — what `git clone`/`git ls-remote` use — isn't subject
/// to that quota, and a commit sha is an unambiguous, single-format signature with no release-vs-
/// branch mode-flapping possible.
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
        (string owner, string repo) = ParseOwnerRepo(input);
        string cloneUrl = $"https://github.com/{owner}/{repo}.git";
        string cacheDir = AddonPaths.AddonRepoCacheDir(owner, repo);

        string headSha = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using Repository repo2 = EnsureUpToDateClone(cloneUrl, cacheDir);
            return repo2.Head.Tip.Sha;
        }, ct);

        string contentDir = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}");
        CopyWorkingTree(cacheDir, contentDir);

        return new AddonDownload(contentDir, repo, headSha, AddonSourceKind.GitHub, $"https://github.com/{owner}/{repo}");
    }

    public async Task<string?> GetLatestVersionSignatureAsync(string input, HttpClient http, CancellationToken ct)
    {
        if (!RepoRegex.IsMatch(input))
        {
            return null;
        }

        (string owner, string repo) = ParseOwnerRepo(input);
        string cloneUrl = $"https://github.com/{owner}/{repo}.git";

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return ResolveRemoteHeadSha(cloneUrl);
            }, ct);
        }
        catch (Exception ex)
        {
            _log.Debug($"Git remote HEAD check failed for {owner}/{repo}: {ex.Message}");
            return null;
        }
    }

    private static (string Owner, string Repo) ParseOwnerRepo(string input)
    {
        Match m = RepoRegex.Match(input);
        string owner = m.Groups["owner"].Value;
        string repo = m.Groups["repo"].Value.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        return (owner, repo);
    }

    /// <summary>
    /// The equivalent of `git ls-remote &lt;url&gt; HEAD` — resolves the default branch's current commit
    /// sha over the network only, without touching (or needing) any local clone.
    /// </summary>
    private static string? ResolveRemoteHeadSha(string cloneUrl)
    {
        List<Reference> refs = Repository.ListRemoteReferences(cloneUrl).ToList();
        Reference? head = refs.FirstOrDefault(r => r.CanonicalName == "HEAD");
        if (head is null)
        {
            return null;
        }

        // HEAD is a symbolic ref (e.g. "refs/heads/main"); resolve it to the direct ref carrying the sha.
        Reference? target = refs.FirstOrDefault(r => r.CanonicalName == head.TargetIdentifier);
        return target?.TargetIdentifier;
    }

    /// <summary>
    /// Clones into <paramref name="cacheDir"/> if it isn't already a valid repo there, otherwise
    /// fetches and hard-resets the checked-out branch to the remote's current tip (this cache is a
    /// read-only mirror the launcher owns exclusively, so discarding any local drift is always safe
    /// and correct — equivalent to `git reset --hard origin/&lt;branch&gt;`).
    /// </summary>
    private Repository EnsureUpToDateClone(string cloneUrl, string cacheDir)
    {
        if (Directory.Exists(cacheDir) && !Repository.IsValid(cacheDir))
        {
            _log.Warn($"Addon repo cache at {cacheDir} is corrupt; re-cloning from scratch.");
            DirectoryHelper.DeleteRecursive(cacheDir);
        }

        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
            Repository.Clone(cloneUrl, cacheDir);
            return new Repository(cacheDir);
        }

        var repo = new Repository(cacheDir);
        try
        {
            string branchName = repo.Head.FriendlyName;
            Remote origin = repo.Network.Remotes["origin"];
            Commands.Fetch(repo, origin.Name, Array.Empty<string>(), null, "addon update check");

            Branch? remoteBranch = repo.Branches[$"origin/{branchName}"];
            if (remoteBranch is not null)
            {
                repo.Reset(ResetMode.Hard, remoteBranch.Tip);
            }

            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>Copies a git working tree into a fresh directory, leaving the ".git" folder behind.</summary>
    private static void CopyWorkingTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsUnderGitDir(source, dir))
            {
                continue;
            }

            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string? fileDir = Path.GetDirectoryName(file);
            if (fileDir is not null && IsUnderGitDir(source, fileDir))
            {
                continue;
            }

            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }

    private static bool IsUnderGitDir(string root, string dir)
    {
        string relative = Path.GetRelativePath(root, dir);
        return relative == ".git" || relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
